using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Papery.Pdf.Editing;

/// <summary>A run of words that look alike.</summary>
public sealed record DocxRun(string Text, float FontSize, bool Bold, bool Italic, PdfColor Color);

/// <summary>A paragraph of text or a picture, in reading order.</summary>
/// <param name="Table">Rows of cells, each cell its runs. When set, this block is a table.</param>
/// <param name="ColumnShares">Each column's share of the table's width.</param>
public sealed record DocxBlock(IReadOnlyList<DocxRun> Runs, byte[]? Jpeg = null, double WidthPoints = 0, double HeightPoints = 0, IReadOnlyList<IReadOnlyList<IReadOnlyList<DocxRun>>>? Table = null, IReadOnlyList<double>? ColumnShares = null);

/// <summary>
/// Turns a page's positioned words into paragraphs. A PDF stores where letters sit, not what they mean, so
/// this is a careful guess: lines come from positions, paragraphs from gaps and font changes.
/// </summary>
public static class DocxLayout
{
    private const double ParagraphGap = 1.7;
    private const double FontChange = 1.15;
    private const double ColumnGap = 3;

    public static IReadOnlyList<IReadOnlyList<PdfWord>> Lines(IReadOnlyList<PdfWord> words)
    {
        var lines = new List<List<PdfWord>>();
        foreach (var word in words.OrderBy(w => w.Area.Y + (w.Area.Height / 2)))
        {
            double middle = word.Area.Y + (word.Area.Height / 2);
            var line = lines.LastOrDefault(l => l.Average(w => w.Area.Y) <= middle && l.Average(w => w.Area.Bottom) >= middle);
            if (line == null)
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        return lines.Select(l => (IReadOnlyList<PdfWord>)l.OrderBy(w => w.Area.X).ToArray()).ToArray();
    }

    /// <summary>
    /// A page's text in reading order, as paragraphs and tables, each with how far down the page it starts. Text
    /// columns are read one at a time, and lines whose gaps line up from row to row become tables.
    /// </summary>
    public static IReadOnlyList<(double Y, DocxBlock Block)> Blocks(IReadOnlyList<PdfWord> words)
    {
        var blocks = new List<(double, DocxBlock)>();
        foreach (var section in ReadingOrder(words))
        {
            var lines = Lines(section);
            int start = 0;
            while (start < lines.Count)
            {
                int end = TableEnd(lines, start, out var columns);
                if (end > start)
                {
                    blocks.Add((lines[start].Min(w => w.Area.Y), Table(lines, start, end, columns)));
                    start = end;
                    continue;
                }

                // Plain text runs on until the next table begins.
                int stop = start + 1;
                while (stop < lines.Count && TableEnd(lines, stop, out _) == stop)
                {
                    stop++;
                }

                var text = lines.Skip(start).Take(stop - start).ToArray();
                foreach (var (paragraph, top) in ParagraphsWithTops(text))
                {
                    blocks.Add((top, new DocxBlock(paragraph)));
                }

                start = stop;
            }
        }

        return blocks;
    }

    private static IEnumerable<(IReadOnlyList<DocxRun> Runs, double Top)> ParagraphsWithTops(IReadOnlyList<IReadOnlyList<PdfWord>> lines)
    {
        var current = new List<DocxRun>();
        double top = lines[0].Min(w => w.Area.Y);
        IReadOnlyList<PdfWord>? previous = null;
        foreach (var line in lines)
        {
            if (previous != null && StartsNewParagraph(previous, line))
            {
                yield return (current, top);
                current = [];
                top = line.Min(w => w.Area.Y);
            }

            AddLine(current, line);
            previous = line;
        }

        if (current.Count > 0)
        {
            yield return (current, top);
        }
    }

    /// <summary>Splits a page with text columns into pieces to read in turn. A line crossing the gutter, like a title, keeps its place between them.</summary>
    private static IReadOnlyList<IReadOnlyList<PdfWord>> ReadingOrder(IReadOnlyList<PdfWord> words)
    {
        if (FindGutter(words) is not double gutter)
        {
            return [words];
        }

        var sections = new List<IReadOnlyList<PdfWord>>();
        var left = new List<PdfWord>();
        var right = new List<PdfWord>();
        void Flush()
        {
            if (left.Count > 0)
            {
                sections.Add(left.ToArray());
            }

            if (right.Count > 0)
            {
                sections.Add(right.ToArray());
            }

            left.Clear();
            right.Clear();
        }

        foreach (var line in Lines(words))
        {
            if (line.Any(w => w.Area.X < gutter && w.Area.Right > gutter))
            {
                Flush();
                sections.Add(line);
                continue;
            }

            foreach (var word in line)
            {
                (word.Area.Right <= gutter ? left : right).Add(word);
            }
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// The clear strip down the middle of a two-column page. Needs real running text on both sides, because a
    /// short two-column table has the same strip and should stay a table.
    /// </summary>
    private static double? FindGutter(IReadOnlyList<PdfWord> words)
    {
        const int Slots = 200, FewestLines = 6, FewestCharacters = 18;
        const double MostCrossing = 0.08, NarrowestStrip = 0.012;
        if (words.Count < 40)
        {
            return null;
        }

        int[] covered = new int[Slots];
        foreach (var word in words)
        {
            int from = Math.Clamp((int)(word.Area.X * Slots), 0, Slots - 1), to = Math.Clamp((int)(word.Area.Right * Slots), 0, Slots - 1);
            for (int i = from; i <= to; i++)
            {
                covered[i]++;
            }
        }

        // The widest nearly empty strip in the middle half of the page.
        int bestStart = -1, bestLength = 0, run = 0;
        for (int i = Slots / 4; i < Slots * 3 / 4; i++)
        {
            run = covered[i] <= words.Count * MostCrossing / 10 ? run + 1 : 0;
            if (run > bestLength)
            {
                bestLength = run;
                bestStart = i - run + 1;
            }
        }

        if (bestLength < NarrowestStrip * Slots)
        {
            return null;
        }

        double gutter = (bestStart + (bestLength / 2.0)) / Slots;
        bool Runs(Func<PdfWord, bool> side)
        {
            var lines = Lines(words.Where(side).ToArray());
            return lines.Count >= FewestLines && lines.Average(l => l.Sum(w => w.Text.Length + 1)) >= FewestCharacters;
        }

        return Runs(w => w.Area.Right <= gutter) && Runs(w => w.Area.X >= gutter) ? gutter : null;
    }

    /// <summary>The words of a line, grouped wherever the gap is too wide to be a space.</summary>
    private static List<List<PdfWord>> Cells(IReadOnlyList<PdfWord> line)
    {
        var cells = new List<List<PdfWord>> { new() { line[0] } };
        for (int i = 1; i < line.Count; i++)
        {
            double perChar = line[i].Area.Width / Math.Max(1, line[i].Text.Length);
            if (line[i].Area.X - line[i - 1].Area.Right > perChar * ColumnGap)
            {
                cells.Add([]);
            }

            cells[^1].Add(line[i]);
        }

        return cells;
    }

    /// <summary>
    /// Where the table starting at a line ends, or that same line if none does. Rows must have their
    /// cells under the same columns and sit close together. One row with gaps isn't a table.
    /// </summary>
    private static int TableEnd(IReadOnlyList<IReadOnlyList<PdfWord>> lines, int start, out List<(double Left, double Right)> columns)
    {
        const int FewestRows = 2, FewestColumns = 2;
        const double FurthestRowGap = 1.6;
        columns = [];
        var first = Cells(lines[start]);
        if (first.Count < FewestColumns)
        {
            return start;
        }

        columns = first.Select(c => (c.Min(w => w.Area.X), c.Max(w => w.Area.Right))).ToList();
        int end = start + 1;
        while (end < lines.Count)
        {
            double height = lines[end - 1].Average(w => w.Area.Height);
            if (lines[end].Min(w => w.Area.Y) - lines[end - 1].Max(w => w.Area.Bottom) > height * FurthestRowGap)
            {
                break;
            }

            var cells = Cells(lines[end]);
            if (cells.Count < FewestColumns)
            {
                break;
            }

            var taken = new HashSet<int>();
            bool fits = true;
            foreach (var cell in cells)
            {
                double left = cell.Min(w => w.Area.X), right = cell.Max(w => w.Area.Right);
                int column = columns.FindIndex(c => c.Left < right && left < c.Right);
                if (column < 0 || !taken.Add(column))
                {
                    fits = false;
                    break;
                }
            }

            if (!fits)
            {
                break;
            }

            foreach (var cell in cells)
            {
                double left = cell.Min(w => w.Area.X), right = cell.Max(w => w.Area.Right);
                int column = columns.FindIndex(c => c.Left < right && left < c.Right);
                columns[column] = (Math.Min(columns[column].Left, left), Math.Max(columns[column].Right, right));
            }

            end++;
        }

        return end - start >= FewestRows ? end : start;
    }

    private static DocxBlock Table(IReadOnlyList<IReadOnlyList<PdfWord>> lines, int start, int end, List<(double Left, double Right)> columns)
    {
        var rows = new List<IReadOnlyList<IReadOnlyList<DocxRun>>>();
        for (int i = start; i < end; i++)
        {
            var row = new List<DocxRun>[columns.Count];
            for (int c = 0; c < row.Length; c++)
            {
                row[c] = [];
            }

            foreach (var cell in Cells(lines[i]))
            {
                double left = cell.Min(w => w.Area.X), right = cell.Max(w => w.Area.Right);
                AddLine(row[Math.Max(0, columns.FindIndex(c => c.Left < right && left < c.Right))], cell);
            }

            rows.Add(row);
        }

        var ordered = columns.Select((c, i) => (c.Left, c.Right, i)).OrderBy(c => c.Left).ToArray();
        double total = Math.Max(1e-6, ordered.Sum(c => c.Right - c.Left));
        var reordered = rows.Select(r => (IReadOnlyList<IReadOnlyList<DocxRun>>)ordered.Select(c => r[c.i]).ToArray()).ToArray();
        return new DocxBlock([], Table: reordered, ColumnShares: ordered.Select(c => (c.Right - c.Left) / total).ToArray());
    }

    public static IReadOnlyList<IReadOnlyList<DocxRun>> Paragraphs(IReadOnlyList<PdfWord> words)
    {
        var paragraphs = new List<IReadOnlyList<DocxRun>>();
        var current = new List<DocxRun>();
        IReadOnlyList<PdfWord>? previous = null;
        foreach (var line in Lines(words))
        {
            if (previous != null && StartsNewParagraph(previous, line))
            {
                paragraphs.Add(current);
                current = [];
            }

            AddLine(current, line);
            previous = line;
        }

        if (current.Count > 0)
        {
            paragraphs.Add(current);
        }

        return paragraphs;
    }

    private static bool StartsNewParagraph(IReadOnlyList<PdfWord> before, IReadOnlyList<PdfWord> line)
    {
        double height = before.Average(w => w.Area.Height);
        double gap = line.Min(w => w.Area.Y) - before.Max(w => w.Area.Bottom);
        double sizeA = before.Max(w => w.FontSize), sizeB = line.Max(w => w.FontSize);
        return gap > height * (ParagraphGap - 1) || Math.Max(sizeA, sizeB) > Math.Min(sizeA, sizeB) * FontChange;
    }

    private static void AddLine(List<DocxRun> runs, IReadOnlyList<PdfWord> line)
    {
        for (int i = 0; i < line.Count; i++)
        {
            var word = line[i];
            string text = word.Text;
            if (i > 0)
            {
                // A wide gap on a line is usually a table's column.
                double perChar = word.Area.Width / Math.Max(1, word.Text.Length);
                text = (word.Area.X - line[i - 1].Area.Right > perChar * ColumnGap ? "\t" : " ") + text;
            }
            else if (runs.Count > 0)
            {
                text = " " + text; // lines of a paragraph flow together
            }

            if (runs.Count > 0 && runs[^1] is var last && Same(last, word))
            {
                runs[^1] = last with { Text = last.Text + text };
            }
            else
            {
                runs.Add(new DocxRun(text, word.FontSize, word.Bold, word.Italic, word.Color));
            }
        }
    }

    private static bool Same(DocxRun run, PdfWord word) =>
        Math.Abs(run.FontSize - word.FontSize) < 0.5 && run.Bold == word.Bold && run.Italic == word.Italic && run.Color == word.Color;
}

/// <summary>Writes a .docx file directly: it's a zip of XML, so no library is needed.</summary>
internal static class DocxWriter
{
    private const int PointsToTwips = 20;
    private const int PointsToEmu = 12700;
    private const int PageMargin = 1000;

    public static async Task ExportAsync(PdfDocument document, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        var pictures = new List<byte[]>();
        for (int page = 0; page < document.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = await document.GetPageWordsAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            var placed = await document.GetPlacedPicturesAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            var size = document.PageSizes[page];
            // Text keeps its reading order, which with columns isn't top to bottom. Each picture goes before the first text below it.
            var blocks = DocxLayout.Blocks(words).ToList();
            foreach (var (picture, area) in placed.OrderBy(p => p.Area.Y))
            {
                byte[] jpeg = await Task.Run(() => JpegEncoder.Encode(picture, 85, grey: false), cancellationToken).ConfigureAwait(false);
                int at = blocks.FindIndex(b => b.Block.Jpeg == null && b.Y > area.Y);
                blocks.Insert(at < 0 ? blocks.Count : at, (area.Y, new DocxBlock([], jpeg, area.Width * size.Width, area.Height * size.Height)));
            }

            bool firstBlock = true;
            foreach (var (_, block) in blocks)
            {
                AppendBlock(body, block, pictures, page > 0 && firstBlock, size);
                firstBlock = false;
            }

            progress?.Report((double)(page + 1) / document.PageCount);
        }

        var pageSize = document.PageSizes[0];
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                stream.SetLength(0);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                Add(zip, "[Content_Types].xml", ContentTypes);
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "word/document.xml", Document(body.ToString(), pageSize));
                var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
                for (int i = 0; i < pictures.Count; i++)
                {
                    rels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rIdImg{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{i + 1}.jpg"/>""");
                    var entry = zip.CreateEntry($"word/media/image{i + 1}.jpg", CompressionLevel.NoCompression);
                    await using var to = entry.Open();
                    await to.WriteAsync(pictures[i], cancellationToken).ConfigureAwait(false);
                }

                rels.Append("</Relationships>");
                Add(zip, "word/_rels/document.xml.rels", rels.ToString());
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AppendTable(StringBuilder body, DocxBlock block, PdfPageSize size)
    {
        const string Line = """w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/>""";
        double usable = Math.Max(1, (size.Width * PointsToTwips) - (2 * PageMargin));
        int[] widths = block.ColumnShares!.Select(s => Math.Max(200, (int)(s * usable))).ToArray();
        body.Append(CultureInfo.InvariantCulture, $"""<w:tbl><w:tblPr><w:tblW w:w="{widths.Sum()}" w:type="dxa"/><w:tblBorders><w:top {Line}<w:left {Line}<w:bottom {Line}<w:right {Line}<w:insideH {Line}<w:insideV {Line}</w:tblBorders><w:tblLayout w:type="fixed"/></w:tblPr><w:tblGrid>""");
        foreach (int width in widths)
        {
            body.Append(CultureInfo.InvariantCulture, $"""<w:gridCol w:w="{width}"/>""");
        }

        body.Append("</w:tblGrid>");
        foreach (var row in block.Table!)
        {
            body.Append("<w:tr>");
            for (int c = 0; c < row.Count; c++)
            {
                body.Append(CultureInfo.InvariantCulture, $"""<w:tc><w:tcPr><w:tcW w:w="{widths[c]}" w:type="dxa"/></w:tcPr><w:p>""");
                AppendRuns(body, row[c]);
                body.Append("</w:p></w:tc>");
            }

            body.Append("</w:tr>");
        }

        // Word needs a paragraph after a table, or two tables in a row would melt into one.
        body.Append("</w:tbl><w:p/>");
    }

    private static void AppendBlock(StringBuilder body, DocxBlock block, List<byte[]> pictures, bool pageBreak, PdfPageSize size)
    {
        if (block.Table != null)
        {
            if (pageBreak)
            {
                body.Append("""<w:p><w:r><w:br w:type="page"/></w:r></w:p>""");
            }

            AppendTable(body, block, size);
            return;
        }

        body.Append("<w:p>");
        if (pageBreak)
        {
            body.Append("""<w:r><w:br w:type="page"/></w:r>""");
        }

        if (block.Jpeg is { } jpeg)
        {
            pictures.Add(jpeg);
            int id = pictures.Count;
            long cx = (long)(block.WidthPoints * PointsToEmu), cy = (long)(block.HeightPoints * PointsToEmu);
            body.Append(CultureInfo.InvariantCulture, $"""<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{id}" name="Picture {id}"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:nvPicPr><pic:cNvPr id="{id}" name="Picture {id}"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rIdImg{id}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""");
        }

        AppendRuns(body, block.Runs);
        body.Append("</w:p>");
    }

    private static void AppendRuns(StringBuilder body, IReadOnlyList<DocxRun> runs)
    {
        foreach (var run in runs)
        {
            body.Append("<w:r><w:rPr>");
            if (run.Bold)
            {
                body.Append("<w:b/>");
            }

            if (run.Italic)
            {
                body.Append("<w:i/>");
            }

            body.Append(CultureInfo.InvariantCulture, $"""<w:color w:val="{run.Color.R:X2}{run.Color.G:X2}{run.Color.B:X2}"/><w:sz w:val="{(int)Math.Clamp(Math.Round(run.FontSize * 2), 2, 3276)}"/></w:rPr>""");
            string[] pieces = run.Text.Split('\t');
            for (int i = 0; i < pieces.Length; i++)
            {
                if (i > 0)
                {
                    body.Append("<w:tab/>");
                }

                body.Append(CultureInfo.InvariantCulture, $"""<w:t xml:space="preserve">{Escape(pieces[i])}</w:t>""");
            }

            body.Append("</w:r>");
        }
    }

    /// <summary>XML can't hold most control characters, and PDFs sometimes contain them.</summary>
    private static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (System.Xml.XmlConvert.IsXmlChar(c))
            {
                clean.Append(c);
            }
        }

        return SecurityElement.Escape(clean.ToString());
    }

    private static string Document(string body, PdfPageSize size) => string.Create(CultureInfo.InvariantCulture,
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><w:body>{body}<w:sectPr><w:pgSz w:w="{(int)(size.Width * PointsToTwips)}" w:h="{(int)(size.Height * PointsToTwips)}"/><w:pgMar w:top="1000" w:right="1000" w:bottom="1000" w:left="1000" w:header="500" w:footer="500" w:gutter="0"/></w:sectPr></w:body></w:document>""");

    private const string ContentTypes = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpg" ContentType="image/jpeg"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""";

    private const string RootRels = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""";
}
