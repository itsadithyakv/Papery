using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Papery.Pdf.Editing;

/// <summary>One spreadsheet cell: text, or a number when the words clearly are one.</summary>
public readonly record struct XlsxCell(string Text, double? Number, bool Bold);

/// <summary>
/// Writes a PDF's text to an .xlsx file. Tables found by <see cref="DocxLayout"/> keep their columns, and other
/// text takes a row each. An .xlsx is a zip of XML, so no library is needed.
/// </summary>
internal static partial class XlsxWriter
{
    private const int ExcelCellLimit = 32_767;
    private const int ExcelRowLimit = 1_048_576;
    private const int WidestColumn = 60;

    // Longer plain digits are account or phone numbers. As real numbers they'd lose digits or show as 1.2E+11.
    private const int LongestPlainNumber = 11;

    /// <summary>Returns how many rows were written. With none (a scan with no text layer), nothing is saved.</summary>
    public static async Task<int> ExportAsync(PdfDocument document, bool oneSheet, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var sheets = new List<(string Name, List<IReadOnlyList<XlsxCell>> Rows)>();
        for (int page = 0; page < document.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = Rows(await document.GetPageWordsAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false));
            if (rows.Count > 0)
            {
                if (oneSheet && sheets.Count > 0)
                {
                    sheets[0].Rows.Add([]); // a blank row where one page ends
                    sheets[0].Rows.AddRange(rows);
                }
                else
                {
                    sheets.Add((oneSheet ? "Papery" : $"Page {page + 1}", rows));
                }
            }

            progress?.Report((page + 1.0) / document.PageCount);
        }

        int total = sheets.Sum(s => s.Rows.Count(r => r.Count > 0));
        if (total == 0)
        {
            return 0;
        }

        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                stream.SetLength(0);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "xl/workbook.xml", Workbook(sheets.Select(s => s.Name).ToArray()));
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                Add(zip, "xl/styles.xml", Styles);
                for (int i = 0; i < sheets.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i].Rows));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
            return total;
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>A page's text as spreadsheet rows, in reading order.</summary>
    internal static List<IReadOnlyList<XlsxCell>> Rows(IReadOnlyList<PdfWord> words)
    {
        var rows = new List<IReadOnlyList<XlsxCell>>();
        foreach (var (_, block) in DocxLayout.Blocks(words))
        {
            if (block.Table is { } table)
            {
                rows.AddRange(table.Select(row => (IReadOnlyList<XlsxCell>)row.Select(cell => Cell(cell, allowNumber: true)).ToArray()));
            }
            else if (block.Runs.Count > 0)
            {
                rows.Add([Cell(block.Runs, allowNumber: false)]);
            }
        }

        return rows;
    }

    private static XlsxCell Cell(IReadOnlyList<DocxRun> runs, bool allowNumber)
    {
        string text = string.Concat(runs.Select(r => r.Text)).Replace('\t', ' ').Trim();
        bool bold = runs.Count > 0 && runs.All(r => r.Bold);
        return new XlsxCell(text, allowNumber ? ParseNumber(text) : null, bold);
    }

    /// <summary>The number in a cell like "1,234.50", "$12" or "(45.00)", which accounts use for negatives. Anything doubtful stays text.</summary>
    internal static double? ParseNumber(string text)
    {
        var match = NumberPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        string digits = match.Groups["digits"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        bool leadingZero = digits.Length > 1 && digits[0] == '0' && digits[1] != '.';
        if (leadingZero || digits.TrimStart('-').Split('.')[0].Length > LongestPlainNumber
            || !double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return null;
        }

        return match.Groups["open"].Success || match.Groups["minus"].Success ? -value : value;
    }

    [GeneratedRegex(@"^(?<open>\()?(?<minus>-)?[$€£₹]?\s?(?<digits>\d{1,3}(,\d{3})+(\.\d+)?|\d+(\.\d+)?)(?(open)\))$")]
    private static partial Regex NumberPattern();

    internal static string ColumnName(int index)
    {
        string name = string.Empty;
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    internal static string Sheet(IReadOnlyList<IReadOnlyList<XlsxCell>> rows)
    {
        int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var body = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        if (columns > 0)
        {
            body.Append("<cols>");
            for (int c = 0; c < columns; c++)
            {
                int column = c;
                int longest = rows.Where(r => column < r.Count).Select(r => r[column].Text.Length).DefaultIfEmpty(0).Max();
                body.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{Math.Clamp(longest + 2, 8, WidestColumn)}" customWidth="1"/>""");
            }

            body.Append("</cols>");
        }

        body.Append("<sheetData>");
        for (int r = 0; r < Math.Min(rows.Count, ExcelRowLimit); r++)
        {
            if (rows[r].Count == 0)
            {
                continue;
            }

            body.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                if (cell.Text.Length == 0)
                {
                    continue;
                }

                string where = ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                string style = cell.Bold ? """ s="1" """ : " ";
                if (cell.Number is double number)
                {
                    body.Append(CultureInfo.InvariantCulture, $"""<c r="{where}"{style}><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                }
                else
                {
                    string text = cell.Text.Length > ExcelCellLimit ? cell.Text[..ExcelCellLimit] : cell.Text;
                    body.Append(CultureInfo.InvariantCulture, $"""<c r="{where}"{style}t="inlineStr"><is><t xml:space="preserve">{DocxWriter.Escape(text)}</t></is></c>""");
                }
            }

            body.Append("</row>");
        }

        body.Append("</sheetData></worksheet>");
        return body.ToString();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ContentTypes(int sheets)
    {
        var text = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (int i = 1; i <= sheets; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        return text.Append("</Types>").ToString();
    }

    private const string RootRels = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""";

    private const string Styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""";

    private static string Workbook(IReadOnlyList<string> names)
    {
        var text = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (int i = 0; i < names.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<sheet name="{DocxWriter.Escape(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        }

        return text.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRels(int sheets)
    {
        var text = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (int i = 1; i <= sheets; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        }

        text.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{sheets + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        return text.Append("</Relationships>").ToString();
    }
}
