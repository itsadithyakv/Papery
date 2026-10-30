using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Papery.Pdf.Editing;

/// <summary>A run of words that look alike.</summary>
public sealed record DocxRun(string Text, float FontSize, bool Bold, bool Italic, PdfColor Color);

/// <summary>A paragraph of text or a picture, in reading order.</summary>
public sealed record DocxBlock(IReadOnlyList<DocxRun> Runs, byte[]? Jpeg = null, double WidthPoints = 0, double HeightPoints = 0);

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
            var blocks = new List<(double Y, DocxBlock Block)>();
            foreach (var paragraph in DocxLayout.Paragraphs(words))
            {
                string first = paragraph[0].Text.Trim();
                var firstWord = words.FirstOrDefault(w => first.StartsWith(w.Text, StringComparison.Ordinal));
                blocks.Add((firstWord?.Area.Y ?? 0, new DocxBlock(paragraph)));
            }

            foreach (var (picture, area) in placed)
            {
                byte[] jpeg = await Task.Run(() => JpegEncoder.Encode(picture, 85, grey: false), cancellationToken).ConfigureAwait(false);
                blocks.Add((area.Y, new DocxBlock([], jpeg, area.Width * size.Width, area.Height * size.Height)));
            }

            bool firstBlock = true;
            foreach (var (_, block) in blocks.OrderBy(b => b.Y))
            {
                AppendBlock(body, block, pictures, page > 0 && firstBlock);
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

    private static void AppendBlock(StringBuilder body, DocxBlock block, List<byte[]> pictures, bool pageBreak)
    {
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

        foreach (var run in block.Runs)
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

        body.Append("</w:p>");
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
