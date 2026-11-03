using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Papery.Pdf.Editing;

/// <summary>How a PDF becomes slides.</summary>
/// <param name="PagesAsPictures">Each slide is one picture of its page. It looks exactly like the PDF, but nothing on it can be edited.</param>
/// <param name="PictureDpi">How sharp page pictures are: for scans, pages with no text, and every page with <paramref name="PagesAsPictures"/>.</param>
public sealed record PowerPointOptions(bool PagesAsPictures = false, int PictureDpi = 150);

/// <summary>Slide size in EMU, the unit PowerPoint files measure in (914,400 to the inch).</summary>
internal readonly record struct SlideSize(long Width, long Height);

/// <summary>Where a page lands on a slide, in EMU: scaled to fit and centred.</summary>
/// <param name="PointScale">Slide points for each page point, for font sizes.</param>
internal readonly record struct SlidePlacement(double Left, double Top, double Width, double Height, double PointScale, PdfPageSize Page)
{
    public static SlidePlacement For(PdfPageSize page, SlideSize slide)
    {
        double width = Math.Max(1, page.Width) * PptxWriter.EmuPerPoint, height = Math.Max(1, page.Height) * PptxWriter.EmuPerPoint;
        double scale = Math.Min(slide.Width / width, slide.Height / height);
        return new SlidePlacement((slide.Width - (width * scale)) / 2, (slide.Height - (height * scale)) / 2, width * scale, height * scale, scale, page);
    }

    public (long X, long Y, long Width, long Height) Map(NormalizedRect area) =>
        ((long)Math.Round(Left + (area.X * Width)), (long)Math.Round(Top + (area.Y * Height)),
         Math.Max(1, (long)Math.Round(area.Width * Width)), Math.Max(1, (long)Math.Round(area.Height * Height)));
}

/// <summary>
/// Writes a .pptx file directly, one slide per page: a zip of XML, like <see cref="DocxWriter"/>. Text goes where it was in
/// boxes that can be edited, and pictures where they were. A page with nothing to read, like a scan, becomes one picture.
/// </summary>
internal static class PptxWriter
{
    public const double EmuPerPoint = 12_700;
    private const double EmuPerInch = 914_400;

    // PowerPoint refuses slides outside 1 to 56 inches a side.
    private const double SmallestSide = EmuPerInch;
    private const double LargestSide = 56 * EmuPerInch;

    private const int LongestPictureSide = 4096;
    private const int PictureQuality = 90;

    // PowerPoint starts a line with the font's ascent, above where the letters begin; a PDF's box starts at the letters.
    private const double AscentLift = 0.2;

    // Another font standing in for the PDF's is often a little wider, and a paragraph shouldn't gain a line from it.
    private const double ParagraphRoom = 1.03;
    private const double LastColumnRoom = 1.15;

    private const string Main = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Relations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelations = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Namespaces = $"""xmlns:a="{Drawing}" xmlns:r="{Relations}" xmlns:p="{Main}" """;
    private const string Header = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""";

    public static async Task ExportAsync(PdfDocument document, string outputPath, PowerPointOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = document.PageCount;
        var slide = SizeFor(document.PageSizes[0]);
        string? title = (await document.GetMetadataAsync(cancellationToken).ConfigureAwait(false)).Title;
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                stream.SetLength(0);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

                // Every page makes exactly one slide, so the package's parts are known before any page is read.
                Add(zip, "[Content_Types].xml", ContentTypes(count));
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "docProps/core.xml", Core(title, DateTime.UtcNow));
                Add(zip, "docProps/app.xml", App(count));
                Add(zip, "ppt/presentation.xml", Presentation(count, slide));
                Add(zip, "ppt/_rels/presentation.xml.rels", PresentationRels(count));
                Add(zip, "ppt/slideMasters/slideMaster1.xml", Master);
                Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", MasterRels);
                Add(zip, "ppt/slideLayouts/slideLayout1.xml", Layout);
                Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", LayoutRels);
                Add(zip, "ppt/theme/theme1.xml", Theme);
                Add(zip, "ppt/presProps.xml", PresProps);
                Add(zip, "ppt/viewProps.xml", ViewProps);
                Add(zip, "ppt/tableStyles.xml", TableStyles);

                // Slides are written as they're read, so a long document's pictures never all sit in memory at once.
                int media = 0;
                for (int page = 0; page < count; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var place = SlidePlacement.For(document.PageSizes[page], slide);
                    var (blocks, pictures) = await ReadPageAsync(document, page, options, cancellationToken).ConfigureAwait(false);
                    var names = new List<string>();
                    foreach (var (jpeg, _) in pictures)
                    {
                        string name = $"image{++media}.jpg";
                        names.Add(name);
                        var entry = zip.CreateEntry("ppt/media/" + name, CompressionLevel.NoCompression);
                        await using var to = entry.Open();
                        await to.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
                    }

                    Add(zip, $"ppt/slides/slide{page + 1}.xml", Slide(place, blocks, pictures.Select(p => p.Area).ToArray()));
                    Add(zip, $"ppt/slides/_rels/slide{page + 1}.xml.rels", SlideRels(names));
                    progress?.Report((page + 1.0) / count);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>A page's text blocks and pictures, or one picture of the whole page when there's no text to edit.</summary>
    private static async Task<(IReadOnlyList<PlacedBlock> Blocks, List<(byte[] Jpeg, NormalizedRect Area)> Pictures)> ReadPageAsync(
        PdfDocument document, int page, PowerPointOptions options, CancellationToken cancellationToken)
    {
        var pictures = new List<(byte[], NormalizedRect)>();
        var (words, turned) = options.PagesAsPictures ? ([], false) : await document.GetVisiblePageWordsAsync(page, cancellationToken).ConfigureAwait(false);
        if (words.Count == 0 || turned)
        {
            // A scan, a drawing, text that doesn't run left to right, or the choice to keep every page as it looks: a
            // picture loses nothing that can be seen, where text boxes would scramble turned text.
            var (width, height) = PicturePixels(document.PageSizes[page], options.PictureDpi);
            var bitmap = await document.RenderPageAsync(page, width, height, PdfRenderOptions.Annotations, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            pictures.Add((await Task.Run(() => JpegEncoder.Encode(bitmap, PictureQuality, grey: false), cancellationToken).ConfigureAwait(false), new NormalizedRect(0, 0, 1, 1)));
            return ([], pictures);
        }

        foreach (var (picture, area) in await document.GetPlacedPicturesAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false))
        {
            pictures.Add((await Task.Run(() => JpegEncoder.Encode(picture, 85, grey: false), cancellationToken).ConfigureAwait(false), area));
        }

        return (DocxLayout.PlacedBlocks(words), pictures);
    }

    internal static SlideSize SizeFor(PdfPageSize page)
    {
        double width = Math.Max(1, page.Width) * EmuPerPoint, height = Math.Max(1, page.Height) * EmuPerPoint;

        // Scaling both sides alike keeps the page's shape. Only a very long, thin page can't fit, and that one is clamped.
        double scale = Math.Max(1, SmallestSide / Math.Min(width, height));
        scale = Math.Min(scale, LargestSide / Math.Max(width, height));
        return new SlideSize(
            (long)Math.Round(Math.Clamp(width * scale, SmallestSide, LargestSide)),
            (long)Math.Round(Math.Clamp(height * scale, SmallestSide, LargestSide)));
    }

    internal static (int Width, int Height) PicturePixels(PdfPageSize page, int dpi)
    {
        double scale = Math.Clamp(dpi, 36, 600) / 72.0;
        double width = Math.Max(1, page.Width) * scale, height = Math.Max(1, page.Height) * scale;
        double fit = Math.Min(1, LongestPictureSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * fit)), Math.Max(1, (int)Math.Round(height * fit)));
    }

    /// <summary>One slide's XML. Pictures come first so text sits on top of them, and picture N uses relationship rId(N + 2).</summary>
    internal static string Slide(SlidePlacement place, IReadOnlyList<PlacedBlock> blocks, IReadOnlyList<NormalizedRect> pictures)
    {
        var xml = new StringBuilder($"""{Header}<p:sld {Namespaces}><p:cSld><p:spTree>{GroupStart}""");
        int id = 2;
        for (int i = 0; i < pictures.Count; i++)
        {
            AppendPicture(xml, id++, i + 2, place.Map(pictures[i]));
        }

        foreach (var block in blocks)
        {
            if (block.Block.Table is { Count: > 0 })
            {
                AppendTable(xml, id++, block, place);
            }
            else if (block.Block.Runs.Count > 0)
            {
                AppendTextBox(xml, id++, block, place);
            }
        }

        xml.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return xml.ToString();
    }

    private static void AppendPicture(StringBuilder xml, int id, int relation, (long X, long Y, long Width, long Height) at) =>
        xml.Append(CultureInfo.InvariantCulture, $"""<p:pic><p:nvPicPr><p:cNvPr id="{id}" name="Picture {id - 1}"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rId{relation}"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="{at.X}" y="{at.Y}"/><a:ext cx="{at.Width}" cy="{at.Height}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>""");

    private static void AppendTextBox(StringBuilder xml, int id, PlacedBlock block, SlidePlacement place)
    {
        var (x, y, width, height) = place.Map(block.Area);
        double largest = block.Block.Runs.Max(r => SlidePoints(r, block, place));
        y -= (long)Math.Round(largest * AscentLift * EmuPerPoint);
        bool oneLine = block.Lines <= 1;
        if (!oneLine)
        {
            width = (long)Math.Round(width * ParagraphRoom);
        }

        xml.Append(CultureInfo.InvariantCulture, $"""<p:sp><p:nvSpPr><p:cNvPr id="{id}" name="Text {id - 1}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{width}" cy="{height}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>""");
        xml.Append(CultureInfo.InvariantCulture, $"""<p:txBody><a:bodyPr wrap="{(oneLine ? "none" : "square")}" lIns="0" tIns="0" rIns="0" bIns="0" rtlCol="0" anchor="t"><a:spAutoFit/></a:bodyPr><a:lstStyle/><a:p>""");

        // The PDF's own line spacing, so a paragraph takes the height it had, unless it's too odd to trust.
        double pitch = block.LinePitch * place.Height / EmuPerPoint;
        bool spaced = !oneLine && pitch >= largest * 0.9 && pitch <= largest * 3;
        long[] stops = TabStops(block, place);
        if (spaced || stops.Length > 0)
        {
            xml.Append("<a:pPr>");
            if (spaced)
            {
                xml.Append(CultureInfo.InvariantCulture, $"""<a:lnSpc><a:spcPts val="{(int)Math.Clamp(Math.Round(pitch * 100), 0, 158_400)}"/></a:lnSpc>""");
            }

            if (stops.Length > 0)
            {
                xml.Append("<a:tabLst>");
                foreach (long stop in stops)
                {
                    xml.Append(CultureInfo.InvariantCulture, $"""<a:tab pos="{stop}" algn="l"/>""");
                }

                xml.Append("</a:tabLst>");
            }

            xml.Append("</a:pPr>");
        }

        AppendRuns(xml, block.Block.Runs, block, place);
        xml.Append(CultureInfo.InvariantCulture, $"""<a:endParaRPr sz="{Hundredths(largest)}"/></a:p></p:txBody></p:sp>""");
    }

    /// <summary>A real PowerPoint table, columns as <see cref="DocxLayout"/> found them, with light lines like the Word export's.</summary>
    private static void AppendTable(StringBuilder xml, int id, PlacedBlock block, SlidePlacement place)
    {
        const string Line = """w="6350"><a:solidFill><a:srgbClr val="BFBFBF"/></a:solidFill>""";
        const string NoStyleNoGrid = "{2D5ABB26-0587-4C30-8999-92F81FD0307C}";
        var rows = block.Block.Table!;
        var (x, y, width, height) = place.Map(block.Area);
        int columns = rows.Max(r => r.Count);
        long[] widths = ColumnWidths(block, columns, place, width);
        long rowHeight = Math.Max(1, block.LinePitch > 0 ? (long)Math.Round(block.LinePitch * place.Height) : height / rows.Count);
        double largest = rows.SelectMany(r => r).SelectMany(c => c).Select(r => SlidePoints(r, block, place)).DefaultIfEmpty(12 * place.PointScale).Max();
        y -= (long)Math.Round(largest * AscentLift * EmuPerPoint);

        xml.Append(CultureInfo.InvariantCulture, $"""<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="{id}" name="Table {id - 1}"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{widths.Sum()}" cy="{rowHeight * rows.Count}"/></p:xfrm>""");
        xml.Append(CultureInfo.InvariantCulture, $"""<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table"><a:tbl><a:tblPr><a:tableStyleId>{NoStyleNoGrid}</a:tableStyleId></a:tblPr><a:tblGrid>""");
        foreach (long column in widths)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<a:gridCol w="{column}"/>""");
        }

        xml.Append("</a:tblGrid>");
        foreach (var row in rows)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<a:tr h="{rowHeight}">""");
            for (int c = 0; c < columns; c++)
            {
                var runs = c < row.Count ? row[c] : [];
                xml.Append("<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p>");
                AppendRuns(xml, runs, block, place);
                double size = runs.Count > 0 ? runs.Max(r => SlidePoints(r, block, place)) : largest;
                xml.Append(CultureInfo.InvariantCulture, $"""<a:endParaRPr sz="{Hundredths(size)}"/></a:p></a:txBody><a:tcPr marL="0" marR="0" marT="0" marB="0"><a:lnL {Line}</a:lnL><a:lnR {Line}</a:lnR><a:lnT {Line}</a:lnT><a:lnB {Line}</a:lnB></a:tcPr></a:tc>""");
            }

            xml.Append("</a:tr>");
        }

        xml.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
    }

    /// <summary>Tab stops where the text after each wide gap began, so it lands there rather than at the next inch.</summary>
    private static long[] TabStops(PlacedBlock block, SlidePlacement place)
    {
        const int MostStops = 32;
        const double Closest = 2 * EmuPerPoint;
        var stops = new List<long>();
        foreach (double stop in block.TabStops ?? [])
        {
            long at = (long)Math.Round(stop * place.Width);
            if (at > 0 && (stops.Count == 0 || at - stops[^1] >= Closest) && stops.Count < MostStops)
            {
                stops.Add(at);
            }
        }

        return [.. stops];
    }

    /// <summary>
    /// Each column runs from where its text starts to where the next column's does, so cells line up with the page. The
    /// gap between two columns goes to the one on its left, as it would with tab stops.
    /// </summary>
    private static long[] ColumnWidths(PlacedBlock block, int columns, SlidePlacement place, long width)
    {
        var lefts = block.Block.ColumnLefts;
        if (lefts == null || lefts.Count != columns)
        {
            var shares = block.Block.ColumnShares;
            return Enumerable.Range(0, columns).Select(c => Math.Max(1, (long)Math.Round((shares != null && c < shares.Count ? shares[c] : 1.0 / columns) * width))).ToArray();
        }

        // The last column ends at its widest text, with no gap after it, so it gets the room a wider stand-in font needs.
        double[] ends = [.. lefts.Skip(1), block.Area.Right];
        var widths = Enumerable.Range(0, columns).Select(c => Math.Max(1, (long)Math.Round((ends[c] - (c == 0 ? block.Area.X : lefts[c])) * place.Width))).ToArray();
        widths[^1] = (long)Math.Round(widths[^1] * LastColumnRoom);
        return widths;
    }

    private static void AppendRuns(StringBuilder xml, IReadOnlyList<DocxRun> runs, PlacedBlock block, SlidePlacement place)
    {
        foreach (var run in runs)
        {
            string text = DocxWriter.Escape(run.Text);
            if (text.Length == 0)
            {
                continue;
            }

            string typeface = Typeface(run.FontName);
            xml.Append(CultureInfo.InvariantCulture, $"""<a:r><a:rPr sz="{Hundredths(SlidePoints(run, block, place))}" b="{(run.Bold ? 1 : 0)}" i="{(run.Italic ? 1 : 0)}"><a:solidFill><a:srgbClr val="{run.Color.R:X2}{run.Color.G:X2}{run.Color.B:X2}"/></a:solidFill>""");
            if (typeface.Length > 0)
            {
                xml.Append(CultureInfo.InvariantCulture, $"""<a:latin typeface="{DocxWriter.Escape(typeface)}"/>""");
            }

            xml.Append(CultureInfo.InvariantCulture, $"""</a:rPr><a:t>{text}</a:t></a:r>""");
        }
    }

    /// <summary>
    /// A run's size on the slide. PDFium reports the size before the text's own scaling, so text drawn at size 1 and
    /// scaled up comes back as 1; then the letters' height on the page is the better guess.
    /// </summary>
    private static double SlidePoints(DocxRun run, PlacedBlock block, SlidePlacement place)
    {
        double lineHeight = block.Lines < 2 ? block.Area.Height * place.Page.Height / 0.8 : block.LinePitch * place.Page.Height / 1.2;
        double points = run.FontSize >= lineHeight * 0.4 ? run.FontSize : lineHeight;
        return points * place.PointScale;
    }

    /// <summary>DrawingML sizes are in hundredths of a point, from 1 to 4000 points.</summary>
    private static int Hundredths(double points) => (int)Math.Clamp(Math.Round(points * 100), 100, 400_000);

    /// <summary>
    /// A font name PowerPoint can look up, from the PDF's own: "ABCDEF+TimesNewRomanPS-BoldMT" is "Times New Roman". Bold
    /// and italic are set on the run, so they come off the name. An empty answer leaves the theme's font.
    /// </summary>
    internal static string Typeface(string pdfName)
    {
        string name = pdfName.Trim();
        if (name.Length >= 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
        {
            name = name[7..]; // the tag a PDF puts on a font it carries only part of
        }

        int cut = name.IndexOfAny([',', '-']);
        if (cut >= 0)
        {
            name = name[..cut];
        }

        foreach (string suffix in (string[])["PSMT", "MT", "PS"])
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        // Words run together in PostScript names; a capital after a small letter starts the next one.
        var spaced = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                spaced.Append(' ');
            }

            spaced.Append(name[i]);
        }

        var words = spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && words[^1] is "Bold" or "Italic" or "Oblique" or "Regular")
        {
            words.RemoveAt(words.Count - 1);
        }

        string result = string.Join(' ', words);
        return result.Length > 0 && char.IsLetter(result[0]) ? result : string.Empty;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ContentTypes(int slides)
    {
        const string Ml = "application/vnd.openxmlformats-officedocument.presentationml";
        var text = new StringBuilder($"""{Header}<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="jpg" ContentType="image/jpeg"/>""");
        text.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/ppt/presentation.xml" ContentType="{Ml}.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="{Ml}.slideMaster+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="{Ml}.slideLayout+xml"/>""");
        text.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/ppt/presProps.xml" ContentType="{Ml}.presProps+xml"/><Override PartName="/ppt/viewProps.xml" ContentType="{Ml}.viewProps+xml"/><Override PartName="/ppt/tableStyles.xml" ContentType="{Ml}.tableStyles+xml"/>""");
        text.Append("""<Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/><Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>""");
        for (int i = 1; i <= slides; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/ppt/slides/slide{i}.xml" ContentType="{Ml}.slide+xml"/>""");
        }

        return text.Append("</Types>").ToString();
    }

    private const string RootRels = $"""{Header}<Relationships xmlns="{PackageRelations}"><Relationship Id="rId1" Type="{Relations}/officeDocument" Target="ppt/presentation.xml"/><Relationship Id="rId2" Type="{PackageRelations}/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="{Relations}/extended-properties" Target="docProps/app.xml"/></Relationships>""";

    private static string Core(string? title, DateTime now)
    {
        string when = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string named = string.IsNullOrWhiteSpace(title) ? string.Empty : $"<dc:title>{DocxWriter.Escape(title)}</dc:title>";
        return $"""{Header}<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">{named}<dcterms:created xsi:type="dcterms:W3CDTF">{when}</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">{when}</dcterms:modified></cp:coreProperties>""";
    }

    private static string App(int slides) => string.Create(CultureInfo.InvariantCulture,
        $"""{Header}<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Papery</Application><PresentationFormat>Custom</PresentationFormat><Slides>{slides}</Slides></Properties>""");

    private static string Presentation(int slides, SlideSize size)
    {
        // Masters and layouts share one run of ids from 2^31 up; slides have their own from 256.
        var text = new StringBuilder($"""{Header}<p:presentation {Namespaces}saveSubsetFonts="1"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>""");
        for (int i = 0; i < slides; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<p:sldId id="{256 + i}" r:id="rId{i + 2}"/>""");
        }

        // Notes pages are portrait A4-ish, as PowerPoint's own default.
        text.Append(CultureInfo.InvariantCulture, $"""</p:sldIdLst><p:sldSz cx="{size.Width}" cy="{size.Height}"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""");
        return text.ToString();
    }

    private static string PresentationRels(int slides)
    {
        var text = new StringBuilder($"""{Header}<Relationships xmlns="{PackageRelations}"><Relationship Id="rId1" Type="{Relations}/slideMaster" Target="slideMasters/slideMaster1.xml"/>""");
        for (int i = 1; i <= slides; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i + 1}" Type="{Relations}/slide" Target="slides/slide{i}.xml"/>""");
        }

        text.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{slides + 2}" Type="{Relations}/presProps" Target="presProps.xml"/><Relationship Id="rId{slides + 3}" Type="{Relations}/viewProps" Target="viewProps.xml"/><Relationship Id="rId{slides + 4}" Type="{Relations}/theme" Target="theme/theme1.xml"/><Relationship Id="rId{slides + 5}" Type="{Relations}/tableStyles" Target="tableStyles.xml"/>""");
        return text.Append("</Relationships>").ToString();
    }

    private static string SlideRels(List<string> media)
    {
        var text = new StringBuilder($"""{Header}<Relationships xmlns="{PackageRelations}"><Relationship Id="rId1" Type="{Relations}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>""");
        for (int i = 0; i < media.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i + 2}" Type="{Relations}/image" Target="../media/{media[i]}"/>""");
        }

        return text.Append("</Relationships>").ToString();
    }

    private const string GroupStart = """<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>""";

    private const string Master = $"""{Header}<p:sldMaster {Namespaces}><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{GroupStart}</p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz="4400"/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz="1800"/></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz="1800"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>""";

    private const string MasterRels = $"""{Header}<Relationships xmlns="{PackageRelations}"><Relationship Id="rId1" Type="{Relations}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="{Relations}/theme" Target="../theme/theme1.xml"/></Relationships>""";

    private const string Layout = $"""{Header}<p:sldLayout {Namespaces}type="blank" preserve="1"><p:cSld name="Blank"><p:spTree>{GroupStart}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>""";

    private const string LayoutRels = $"""{Header}<Relationships xmlns="{PackageRelations}"><Relationship Id="rId1" Type="{Relations}/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""";

    private const string PresProps = $"""{Header}<p:presentationPr {Namespaces}/>""";

    private const string ViewProps = $"""{Header}<p:viewPr {Namespaces}><p:normalViewPr><p:restoredLeft sz="15620"/><p:restoredTop sz="94660"/></p:normalViewPr><p:gridSpacing cx="76200" cy="76200"/></p:viewPr>""";

    private const string TableStyles = $$"""{{Header}}<a:tblStyleLst xmlns:a="{{Drawing}}" def="{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}"/>""";

    // The schema asks for three of each style; plain ones do, since slides set their own looks.
    private const string Fill = """<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>""";
    private const string Theme = $"""{Header}<a:theme xmlns:a="{Drawing}" name="Papery"><a:themeElements><a:clrScheme name="Papery"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme><a:fontScheme name="Papery"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Papery"><a:fillStyleLst>{Fill}{Fill}{Fill}</a:fillStyleLst><a:lnStyleLst><a:ln w="6350">{Fill}</a:ln><a:ln w="12700">{Fill}</a:ln><a:ln w="19050">{Fill}</a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>{Fill}{Fill}{Fill}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>""";
}
