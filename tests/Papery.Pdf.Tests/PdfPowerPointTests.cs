using System.Globalization;
using System.IO.Compression;
using System.Xml;
using Papery.Pdf.Editing;
using PdfSharp.Drawing;

namespace Papery.Pdf.Tests;

public sealed class PdfPowerPointTests : IDisposable
{
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath(string extension = ".pdf") => Path.Combine(_files.Folder, $"t-{Guid.NewGuid():N}{extension}");

    /// <summary>A PDF with pages of the given sizes, each with one line of text at the top left, and more on the first.</summary>
    private string CreatePages(params (double Width, double Height, string Text)[] pages)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        foreach (var (width, height, text) in pages)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString(text, new XFont("Arial", 28, XFontStyleEx.Bold), XBrushes.Black, 72, 90);
            var body = new XFont("Arial", 12);
            g.DrawString("First line of a paragraph and more", body, XBrushes.Black, 72, 150);
            g.DrawString("second line of the same paragraph", body, XBrushes.Black, 72, 166);
        }

        string path = NewPath();
        doc.Save(path);
        return path;
    }

    private string CreateLetter() => CreatePages((600, 800, "The Big Heading"), (600, 800, "Second page words"));

    private async Task<string> CreateScanAsync()
    {
        byte[] pixels = new byte[40 * 40 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }

        var picture = new PdfBitmap(40, 40, 160, pixels);
        string path = NewPath();
        await Engine.ImagesToPdfAsync([new PdfImagePage(600, 800, () => Task.FromResult(new PdfImageData(null, picture)))], new ImagePageOptions(), path);
        return path;
    }

    private async Task<string> ExportAsync(string pdf, PowerPointOptions? options = null)
    {
        string output = NewPath(".pptx");
        await using var doc = await Engine.OpenAsync(pdf);
        await Engine.ExportPowerPointAsync(doc, output, options);
        return output;
    }

    private static XmlDocument Part(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.True(entry != null, $"{name} is missing");
        var xml = new XmlDocument();
        using var stream = entry.Open();
        xml.Load(stream);
        return xml;
    }

    private static XmlNamespaceManager Names(XmlDocument xml)
    {
        var names = new XmlNamespaceManager(xml.NameTable);
        names.AddNamespace("p", P);
        names.AddNamespace("a", A);
        names.AddNamespace("pr", "http://schemas.openxmlformats.org/package/2006/relationships");
        names.AddNamespace("ct", "http://schemas.openxmlformats.org/package/2006/content-types");
        return names;
    }

    private static long Number(XmlElement element, string attribute) => long.Parse(element.GetAttribute(attribute), CultureInfo.InvariantCulture);

    private static string SlideText(ZipArchive zip, int slide)
    {
        var xml = Part(zip, $"ppt/slides/slide{slide}.xml");
        return string.Join(" ", xml.SelectNodes("//a:t", Names(xml))!.Cast<XmlNode>().Select(n => n.InnerText));
    }

    /// <summary>A relationship's target as a part name, from the folder the relationship's source is in.</summary>
    private static string Resolve(string relsName, string target)
    {
        string source = relsName.Replace("_rels/", string.Empty, StringComparison.Ordinal);
        var parts = target.StartsWith('/') ? [] : source.Split('/').SkipLast(1).ToList();
        foreach (string piece in target.TrimStart('/').Split('/'))
        {
            if (piece == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (piece != ".")
            {
                parts.Add(piece);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>What PowerPoint insists on: every part present, parsed, typed, and every link pointing at a real part.</summary>
    private static void CheckPackage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string[] required =
        [
            "[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "docProps/app.xml", "ppt/presentation.xml", "ppt/_rels/presentation.xml.rels",
            "ppt/slideMasters/slideMaster1.xml", "ppt/slideMasters/_rels/slideMaster1.xml.rels", "ppt/slideLayouts/slideLayout1.xml",
            "ppt/slideLayouts/_rels/slideLayout1.xml.rels", "ppt/theme/theme1.xml",
        ];
        foreach (string name in required)
        {
            Assert.NotNull(zip.GetEntry(name));
        }

        var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            _ = Part(zip, entry.FullName); // throws if it isn't well-formed
        }

        foreach (var rels in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var xml = Part(zip, rels.FullName);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElement link in xml.SelectNodes("//pr:Relationship", Names(xml))!)
            {
                Assert.True(ids.Add(link.GetAttribute("Id")), $"{rels.FullName} repeats an id");
                string target = Resolve(rels.FullName, link.GetAttribute("Target"));
                Assert.True(names.Contains(target), $"{rels.FullName} points at {target}, which isn't there");
            }
        }

        var types = Part(zip, "[Content_Types].xml");
        var typeNames = Names(types);
        var defaults = types.SelectNodes("//ct:Default", typeNames)!.Cast<XmlElement>().Select(e => e.GetAttribute("Extension")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = types.SelectNodes("//ct:Override", typeNames)!.Cast<XmlElement>().Select(e => e.GetAttribute("PartName").TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        Assert.All(overrides, part => Assert.Contains(part, names));
        foreach (string name in names.Where(n => n != "[Content_Types].xml"))
        {
            Assert.True(overrides.Contains(name) || defaults.Contains(Path.GetExtension(name).TrimStart('.')), $"{name} has no content type");
        }

        // Every slide in the list must be a slide, with the ids PowerPoint allows.
        var presentation = Part(zip, "ppt/presentation.xml");
        var slideIds = presentation.SelectNodes("//p:sldId", Names(presentation))!.Cast<XmlElement>().Select(e => Number(e, "id")).ToArray();
        Assert.All(slideIds, id => Assert.InRange(id, 256, 2_147_483_647));
        Assert.Equal(slideIds.Length, slideIds.Distinct().Count());
    }

    [Fact]
    public async Task A_presentation_has_every_part_and_each_one_is_sound()
    {
        string output = await ExportAsync(CreateLetter());

        CheckPackage(output);
    }

    [Fact]
    public async Task Each_page_makes_one_slide()
    {
        string pdf = CreatePages((600, 800, "One"), (600, 800, "Two"), (600, 800, "Three"));

        string output = await ExportAsync(pdf);

        using var zip = ZipFile.OpenRead(output);
        var presentation = Part(zip, "ppt/presentation.xml");
        Assert.Equal(3, presentation.SelectNodes("//p:sldId", Names(presentation))!.Count);
        Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)));
        Assert.Contains("Three", SlideText(zip, 3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slides_take_the_first_page_shape_and_other_pages_fit_inside()
    {
        string pdf = CreatePages((800, 450, "Wide page"), (450, 800, "Tall page"));

        string output = await ExportAsync(pdf);

        using var zip = ZipFile.OpenRead(output);
        var presentation = Part(zip, "ppt/presentation.xml");
        var size = (XmlElement)presentation.SelectSingleNode("//p:sldSz", Names(presentation))!;
        long cx = Number(size, "cx"), cy = Number(size, "cy");
        Assert.Equal(800 * 12700, cx);
        Assert.Equal(450 * 12700, cy);

        // The tall page is scaled down to the slide's height and centred, so everything on it lies within the slide.
        var slide = Part(zip, "ppt/slides/slide2.xml");
        Assert.NotEmpty(slide.SelectNodes("//p:sp//a:off", Names(slide))!.Cast<XmlNode>());
        foreach (XmlElement offset in slide.SelectNodes("//p:sp//a:off", Names(slide))!)
        {
            long x = Number(offset, "x");
            Assert.InRange(x, (cx - (cy * 450 / 800)) / 2, cx);
        }
    }

    [Fact]
    public void Page_sizes_become_slide_sizes_PowerPoint_accepts()
    {
        Assert.Equal(new SlideSize(595 * 12700, 842 * 12700), PptxWriter.SizeFor(new PdfPageSize(595, 842)));

        var tiny = PptxWriter.SizeFor(new PdfPageSize(36, 18)); // half an inch by a quarter
        Assert.Equal(2.0, tiny.Width / (double)tiny.Height, 3);
        Assert.InRange(tiny.Height, 914_400, 51_206_400);

        var huge = PptxWriter.SizeFor(new PdfPageSize(14_400, 7_200)); // 200 inches wide
        Assert.Equal(51_206_400, huge.Width);
        Assert.Equal(2.0, huge.Width / (double)huge.Height, 3);
    }

    [Fact]
    public async Task Text_goes_into_text_boxes_with_its_size_and_weight()
    {
        string output = await ExportAsync(CreateLetter());

        using var zip = ZipFile.OpenRead(output);
        Assert.Contains("The Big Heading", SlideText(zip, 1), StringComparison.Ordinal);
        Assert.Contains("First line of a paragraph and more second line of the same paragraph", SlideText(zip, 1), StringComparison.Ordinal);
        Assert.Contains("Second page words", SlideText(zip, 2), StringComparison.Ordinal);

        var slide = Part(zip, "ppt/slides/slide1.xml");
        var names = Names(slide);
        var heading = (XmlElement)slide.SelectSingleNode("//a:r[contains(a:t, 'Heading')]/a:rPr", names)!;
        Assert.Equal("1", heading.GetAttribute("b"));
        Assert.InRange(Number(heading, "sz"), 2600, 3000);
        Assert.Equal("Arial", ((XmlElement)heading.SelectSingleNode("a:latin", names)!).GetAttribute("typeface"));
        Assert.Equal(2, slide.SelectNodes("//p:sp[p:nvSpPr/p:cNvSpPr/@txBox='1']", names)!.Count);

        // The heading's box starts where the heading was: an inch in from the left.
        var offset = (XmlElement)slide.SelectSingleNode("//p:sp[contains(., 'Heading')]//a:off", names)!;
        Assert.InRange(Number(offset, "x") / 12700.0, 70, 76);
    }

    [Fact]
    public async Task Lines_in_columns_become_a_table()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 11);
            string[][] rows = [["Item", "Count", "Price"], ["Apples", "3", "1.20"], ["Pears", "12", "4.80"]];
            for (int r = 0; r < rows.Length; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    g.DrawString(rows[r][c], font, XBrushes.Black, 72 + (c * 160), 120 + (r * 16));
                }
            }
        }

        string pdf = NewPath();
        doc.Save(pdf);

        string output = await ExportAsync(pdf);

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        var slide = Part(zip, "ppt/slides/slide1.xml");
        var names = Names(slide);
        Assert.Equal(3, slide.SelectNodes("//a:tbl/a:tr", names)!.Count);
        Assert.Equal(3, slide.SelectNodes("//a:tbl/a:tblGrid/a:gridCol", names)!.Count);
        Assert.Equal("Pears", slide.SelectSingleNode("//a:tbl/a:tr[3]/a:tc[1]", names)!.InnerText);
    }

    [Fact]
    public async Task Pictures_on_a_page_with_text_keep_their_place()
    {
        byte[] pixels = new byte[64 * 64 * 4];
        Array.Fill(pixels, (byte)120);
        byte[] jpeg = JpegEncoder.Encode(new PdfBitmap(64, 64, 256, pixels), 90, grey: false);
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        using (var g = XGraphics.FromPdfPage(page))
        using (var image = XImage.FromStream(new MemoryStream(jpeg)))
        {
            g.DrawString("A caption under the picture", new XFont("Arial", 12), XBrushes.Black, 72, 400);
            g.DrawImage(image, 300, 100, 200, 200);
        }

        string pdf = NewPath();
        doc.Save(pdf);

        string output = await ExportAsync(pdf);

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        Assert.Contains("A caption under the picture", SlideText(zip, 1), StringComparison.Ordinal);
        var slide = Part(zip, "ppt/slides/slide1.xml");
        var names = Names(slide);
        var picture = (XmlElement)slide.SelectSingleNode("//p:pic//a:off", names)!;
        Assert.InRange(Number(picture, "x") / 12700.0, 298, 302);
        Assert.InRange(Number(picture, "y") / 12700.0, 98, 102);
        var size = (XmlElement)slide.SelectSingleNode("//p:pic//a:ext", names)!;
        Assert.InRange(Number(size, "cx") / 12700.0, 198, 202);
        Assert.NotNull(zip.GetEntry("ppt/media/image1.jpg"));
    }

    [Fact]
    public async Task A_page_with_no_text_becomes_one_picture()
    {
        string output = await ExportAsync(await CreateScanAsync());

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        var slide = Part(zip, "ppt/slides/slide1.xml");
        var names = Names(slide);
        Assert.Single(slide.SelectNodes("//p:pic", names)!.Cast<XmlNode>());
        Assert.Empty(slide.SelectNodes("//a:t", names)!.Cast<XmlNode>());
        var size = (XmlElement)slide.SelectSingleNode("//p:pic//a:ext", names)!;
        Assert.Equal(600 * 12700, Number(size, "cx"));
        Assert.Equal(800 * 12700, Number(size, "cy"));
    }

    [Fact]
    public async Task A_scan_with_a_hidden_text_layer_shows_only_the_picture()
    {
        string path = await CreateScanAsync();
        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, [new OcrWord("Lighthouse", new NormalizedRect(0.1, 0.1, 0.2, 0.03))])], path);
        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Contains("Lighthouse", await doc.GetPageTextAsync(0), StringComparison.Ordinal); // the layer is really there
        }

        string output = await ExportAsync(path);

        using var zip = ZipFile.OpenRead(output);
        Assert.DoesNotContain("Lighthouse", SlideText(zip, 1), StringComparison.Ordinal);
        var slide = Part(zip, "ppt/slides/slide1.xml");
        Assert.Single(slide.SelectNodes("//p:pic", Names(slide))!.Cast<XmlNode>());
    }

    [Fact]
    public async Task The_scanned_test_document_becomes_pictures()
    {
        string? folder = TestFilesFolder();
        string scan = Path.Combine(folder ?? string.Empty, "scanned-document.pdf");
        if (folder == null || !File.Exists(scan))
        {
            return; // the set isn't there, as on a machine with only the code
        }

        int pages;
        await using (var doc = await Engine.OpenAsync(scan))
        {
            pages = doc.PageCount;
        }

        string output = await ExportAsync(scan);

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        for (int i = 1; i <= pages; i++)
        {
            var slide = Part(zip, $"ppt/slides/slide{i}.xml");
            Assert.NotEmpty(slide.SelectNodes("//p:pic", Names(slide))!.Cast<XmlNode>());
        }
    }

    /// <summary>Real-world shapes of PDF from the repo's test set: rotations, odd sizes, forms, links and layers.</summary>
    [Fact]
    public async Task Every_ordinary_test_file_makes_a_sound_presentation()
    {
        const long Largest = 10 * 1024 * 1024;
        string? folder = TestFilesFolder();
        if (folder == null)
        {
            return; // the set isn't there, as on a machine with only the code
        }

        var checkedFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.pdf"))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("damaged", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length > Largest)
            {
                continue;
            }

            string output = await ExportAsync(file);
            CheckPackage(output);
            checkedFiles.Add(name);
        }

        Assert.Contains("mixed-sizes-and-rotations.pdf", checkedFiles);
    }

    private static string? TestFilesFolder()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder != null && !Directory.Exists(Path.Combine(folder, "PaperyTestFiles")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return folder == null ? null : Path.Combine(folder, "PaperyTestFiles");
    }

    private string CreateTurned(int pageRotation, double textTurn)
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        page.Rotate = pageRotation;
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.RotateAtTransform(textTurn, new XPoint(300, 400));
            g.DrawString("Turning words here", new XFont("Arial", 20), XBrushes.Black, 250, 400);
        }

        string path = NewPath();
        doc.Save(path);
        return path;
    }

    /// <summary>
    /// Text is turned exactly when it doesn't read left to right on the page as shown, whichever way the page and the
    /// text are each rotated. The way it reads comes from where PDFium puts its first and last letters.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Turned_text_is_found_however_the_page_is_rotated(int pageRotation)
    {
        foreach (double turn in (double[])[0, 90, 180, 270])
        {
            await using var doc = await Engine.OpenAsync(CreateTurned(pageRotation, turn));
            var (words, turned) = await doc.GetVisiblePageWordsAsync(0);
            var word = words.First(w => w.Text == "Turning");
            var first = (await doc.GetSelectionAsync(0, word.Start, word.Start)).Areas[0];
            var last = (await doc.GetSelectionAsync(0, word.Start + 6, word.Start + 6)).Areas[0];
            bool readsRight = last.X - first.X > Math.Abs(last.Y - first.Y);
            Assert.True(turned != readsRight, $"page turned {pageRotation}, text {turn}: turned={turned}, reads right={readsRight}");
        }
    }

    [Fact]
    public async Task A_page_with_turned_text_becomes_a_picture_and_upright_text_stays_text()
    {
        string sideways = await ExportAsync(CreateTurned(0, 90));
        string upright = await ExportAsync(CreateTurned(180, 180)); // upside down twice over

        using (var zip = ZipFile.OpenRead(sideways))
        {
            var slide = Part(zip, "ppt/slides/slide1.xml");
            Assert.Single(slide.SelectNodes("//p:pic", Names(slide))!.Cast<XmlNode>());
            Assert.Empty(SlideText(zip, 1).Trim());
        }

        using (var zip = ZipFile.OpenRead(upright))
        {
            Assert.Contains("Turning words here", SlideText(zip, 1), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Text_after_a_wide_gap_keeps_its_place_with_a_tab_stop()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 12);
            g.DrawString("Chapter one", font, XBrushes.Black, 72, 100);
            g.DrawString("page 2", font, XBrushes.Black, 400, 100);
        }

        string pdf = NewPath();
        doc.Save(pdf);

        string output = await ExportAsync(pdf);

        using var zip = ZipFile.OpenRead(output);
        var slide = Part(zip, "ppt/slides/slide1.xml");
        var names = Names(slide);
        Assert.Contains("Chapter one\tpage 2", SlideText(zip, 1), StringComparison.Ordinal);
        var stop = (XmlElement)slide.SelectSingleNode("//a:tabLst/a:tab", names)!;
        Assert.InRange(Number(stop, "pos") / 12700.0, 400 - 72 - 2, 400 - 72 + 2);
    }

    [Fact]
    public async Task Pictures_of_pages_leave_nothing_to_edit()
    {
        string output = await ExportAsync(CreateLetter(), new PowerPointOptions(PagesAsPictures: true, PictureDpi: 72));

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        for (int i = 1; i <= 2; i++)
        {
            var slide = Part(zip, $"ppt/slides/slide{i}.xml");
            Assert.Single(slide.SelectNodes("//p:pic", Names(slide))!.Cast<XmlNode>());
            Assert.Empty(SlideText(zip, i).Trim());
        }

        Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelling_leaves_no_file_and_keeps_the_old_one()
    {
        string pdf = CreatePages((600, 800, "One"), (600, 800, "Two"), (600, 800, "Three"));
        string output = NewPath(".pptx");
        await File.WriteAllTextAsync(output, "older file");
        string fresh = NewPath(".pptx");
        await using var doc = await Engine.OpenAsync(pdf);
        using var cancel = new CancellationTokenSource();

        // Cancelled after the first slide, so the zip is already half written.
        var stopAfterOne = new Stopper(cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine.ExportPowerPointAsync(doc, output, null, stopAfterOne, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine.ExportPowerPointAsync(doc, fresh, null, null, cancel.Token));

        Assert.Equal("older file", await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(fresh));
        Assert.Empty(Directory.GetFiles(_files.Folder, "*.papery-tmp*"));
    }

    /// <summary>Cancels at once, on the thread doing the work, rather than later the way <see cref="Progress{T}"/> would.</summary>
    private sealed class Stopper(CancellationTokenSource cancel) : IProgress<double>
    {
        public void Report(double value) => cancel.Cancel();
    }

    [Fact]
    public async Task Characters_XML_reserves_are_escaped()
    {
        string pdf = CreatePages((600, 800, "Fish & Chips <b> \"quoted\" 'single'"));

        string output = await ExportAsync(pdf);

        CheckPackage(output);
        using var zip = ZipFile.OpenRead(output);
        Assert.Contains("Fish & Chips <b> \"quoted\" 'single'", SlideText(zip, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void Characters_XML_cannot_hold_are_dropped_and_others_kept()
    {
        var run = new DocxRun("Bell\u0007 nul\u0000 bad￾ half\uD800 rocket \U0001F680 tab\tend", 12, false, false, new PdfColor(0, 0, 0), "Arial");
        var block = new PlacedBlock(new NormalizedRect(0.1, 0.1, 0.5, 0.02), new DocxBlock([run]), 1, 0);
        var place = SlidePlacement.For(new PdfPageSize(600, 800), PptxWriter.SizeFor(new PdfPageSize(600, 800)));

        string xml = PptxWriter.Slide(place, [block], []);

        var slide = new XmlDocument();
        slide.LoadXml(xml);
        string text = slide.SelectSingleNode("//a:t", Names(slide))!.InnerText;
        Assert.Equal("Bell nul bad half rocket \U0001F680 tab\tend", text);
    }

    [Theory]
    [InlineData("ABCDEF+TimesNewRomanPS-BoldMT", "Times New Roman")]
    [InlineData("ArialMT", "Arial")]
    [InlineData("Calibri,Bold", "Calibri")]
    [InlineData("SegoeUI-Italic", "Segoe UI")]
    [InlineData("Helvetica", "Helvetica")]
    [InlineData("Times New Roman", "Times New Roman")]
    [InlineData("", "")]
    [InlineData("ABCDEF+", "")]
    public void Font_names_are_turned_into_ones_PowerPoint_knows(string pdfName, string expected) =>
        Assert.Equal(expected, PptxWriter.Typeface(pdfName));
}
