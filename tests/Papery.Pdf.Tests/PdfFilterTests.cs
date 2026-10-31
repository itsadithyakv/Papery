using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfFilterTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath => Path.Combine(_files.Folder, $"f-{Guid.NewGuid():N}.pdf");

    /// <summary>A pretend scan: yellowed paper, a shadow growing towards the right, and lines of grey "text".</summary>
    private static PdfBitmap Scan(int width = 800, int height = 1000, double tilt = 0)
    {
        var random = new Random(7);
        byte[] pixels = new byte[width * height * 4];
        double slope = Math.Tan(tilt * Math.PI / 180);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                double shadow = 1 - (0.45 * x / width);
                double row = y + (x * slope);
                bool ink = x > 60 && x < width - 60 && row > 60 && row < height - 60 && row % 40 < 12 && x % 11 < 8;
                int grain = random.Next(-5, 6);
                pixels[at] = (byte)Math.Clamp(((ink ? 60 : 195) * shadow) + grain, 0, 255);
                pixels[at + 1] = (byte)Math.Clamp(((ink ? 60 : 225) * shadow) + grain, 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(((ink ? 60 : 235) * shadow) + grain, 0, 255);
                pixels[at + 3] = 255;
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    private static (double B, double G, double R) Average(PdfBitmap picture, int left, int top, int right, int bottom)
    {
        double b = 0, g = 0, r = 0;
        int count = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int at = (y * picture.Stride) + (x * 4);
                b += picture.Pixels[at];
                g += picture.Pixels[at + 1];
                r += picture.Pixels[at + 2];
                count++;
            }
        }

        return (b / count, g / count, r / count);
    }

    private async Task<string> ScanPdfAsync(PdfBitmap picture, int pages = 1)
    {
        string path = NewPath;
        var list = Enumerable.Range(0, pages).Select(_ => new PdfImagePage(400, 500, () => Task.FromResult(new PdfImageData(null, picture)))).ToArray();
        await Engine.ImagesToPdfAsync(list, new ImagePageOptions(), path);
        return path;
    }

    // ================================================================= the pixels

    [Fact]
    public void Clean_document_removes_shadow_and_yellow_but_keeps_ink_dark()
    {
        var result = PictureFilter.Apply(Scan(), new PictureFilterOptions(PictureLook.Document));

        // Paper in the bright left margin and the shadowed right margin both come out white.
        var bright = Average(result, 5, 400, 40, 600);
        var shaded = Average(result, 760, 400, 795, 600);
        Assert.True(Math.Min(bright.B, Math.Min(bright.G, bright.R)) > 245, $"left margin {bright}");
        Assert.True(Math.Min(shaded.B, Math.Min(shaded.G, shaded.R)) > 245, $"right margin {shaded}");

        // Ink (a text line, in its solid columns) stays much darker than paper.
        int ink = (125 * result.Stride) + (66 * 4);
        Assert.True(result.Pixels[ink] < 120, $"ink is {result.Pixels[ink]}");
    }

    [Fact]
    public void Black_and_white_has_only_black_and_white()
    {
        var result = PictureFilter.Apply(Scan(), new PictureFilterOptions(PictureLook.BlackAndWhite));
        int black = 0;
        for (int i = 0; i < result.Pixels.Length; i += 4)
        {
            Assert.True(result.Pixels[i] is 0 or 255);
            Assert.Equal(result.Pixels[i], result.Pixels[i + 1]);
            Assert.Equal(result.Pixels[i], result.Pixels[i + 2]);
            black += result.Pixels[i] == 0 ? 1 : 0;
        }

        // The text survives, and the shadow doesn't turn into a black slab.
        double share = (double)black / (result.Width * result.Height);
        Assert.InRange(share, 0.05, 0.3);
        var shaded = Average(result, 760, 400, 795, 600);
        Assert.Equal(255, shaded.G);
    }

    [Fact]
    public void A_big_photo_on_a_page_is_not_washed_out()
    {
        const int Side = 600;
        byte[] pixels = new byte[Side * Side * 4];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                int at = ((y * Side) + x) * 4;
                byte value = x is > 100 and < 500 && y is > 100 and < 500 ? (byte)60 : (byte)230;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        var result = PictureFilter.Apply(new PdfBitmap(Side, Side, Side * 4, pixels), new PictureFilterOptions(PictureLook.Document));
        Assert.True(Average(result, 250, 250, 350, 350).G < 170, "the middle of the dark photo must stay dark");
    }

    [Fact]
    public void Auto_removes_a_colour_cast()
    {
        var result = PictureFilter.Apply(Scan(), new PictureFilterOptions(PictureLook.Auto));
        var paper = Average(result, 5, 400, 40, 600);
        Assert.True(Math.Abs(paper.B - paper.R) < 30, $"paper is {paper}");
    }

    [Fact]
    public void Grey_has_no_colour()
    {
        var result = PictureFilter.Apply(Scan(), new PictureFilterOptions(PictureLook.Grey));
        for (int i = 0; i < result.Pixels.Length; i += 4)
        {
            Assert.Equal(result.Pixels[i], result.Pixels[i + 2]);
        }
    }

    [Fact]
    public void Brightness_and_contrast_go_the_right_way()
    {
        var scan = Scan();
        double Mean(PdfBitmap p) => Average(p, 0, 0, p.Width, p.Height).G;
        Assert.True(Mean(PictureFilter.Apply(scan, new PictureFilterOptions(PictureLook.Original, Brightness: 40))) > Mean(scan) + 20);
        Assert.True(Mean(PictureFilter.Apply(scan, new PictureFilterOptions(PictureLook.Original, Brightness: -40))) < Mean(scan) - 20);

        double Spread(PdfBitmap p) => Math.Abs(Average(p, 5, 400, 40, 600).G - p.Pixels[(125 * p.Stride) + (66 * 4) + 1]);
        Assert.True(Spread(PictureFilter.Apply(scan, new PictureFilterOptions(PictureLook.Original, Contrast: 40))) > Spread(scan));
    }

    [Fact]
    public void Sharpen_steepens_edges_and_smooth_removes_grain()
    {
        var scan = Scan();
        double Rough(PdfBitmap p, int left, int top, int right, int bottom)
        {
            double total = 0;
            int count = 0;
            for (int y = top; y < bottom; y++)
            {
                for (int x = left + 1; x < right; x++)
                {
                    total += Math.Abs(p.Pixels[(y * p.Stride) + (x * 4)] - p.Pixels[(y * p.Stride) + ((x - 1) * 4)]);
                    count++;
                }
            }

            return total / count;
        }

        // Blank paper: only grain to see.
        Assert.True(Rough(PictureFilter.Apply(scan, new PictureFilterOptions(PictureLook.Original, Smooth: 60)), 5, 5, 50, 50) < Rough(scan, 5, 5, 50, 50) * 0.6);

        // A text line: mostly edges.
        Assert.True(Rough(PictureFilter.Apply(scan, new PictureFilterOptions(PictureLook.Original, Sharpen: 80)), 100, 122, 700, 130) > Rough(scan, 100, 122, 700, 130));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-3.0)]
    public void Straighten_undoes_a_tilt(double tilt)
    {
        var crooked = Scan(tilt: tilt);
        Assert.True(Math.Abs(PictureFilter.FindSkew(crooked.Pixels, crooked.Width, crooked.Height)) > 1);

        var result = PictureFilter.Apply(crooked, new PictureFilterOptions(PictureLook.Original, Straighten: true));
        Assert.InRange(PictureFilter.FindSkew(result.Pixels, result.Width, result.Height), -0.3, 0.3);
    }

    [Fact]
    public void Straighten_leaves_straight_pages_and_photos_alone()
    {
        var straight = Scan();
        Assert.InRange(PictureFilter.FindSkew(straight.Pixels, straight.Width, straight.Height), -0.1, 0.1);

        var random = new Random(3);
        byte[] noise = new byte[400 * 400 * 4];
        random.NextBytes(noise);
        Assert.Equal(0, PictureFilter.FindSkew(noise, 400, 400));
    }

    [Theory]
    [InlineData(PictureLook.Original)]
    [InlineData(PictureLook.Auto)]
    [InlineData(PictureLook.Document)]
    [InlineData(PictureLook.Grey)]
    [InlineData(PictureLook.BlackAndWhite)]
    [InlineData(PictureLook.Vivid)]
    public void Odd_pictures_do_not_break_a_look(PictureLook look)
    {
        foreach (var (w, h) in new[] { (1, 1), (3, 500), (500, 3), (64, 64) })
        {
            foreach (byte fill in new byte[] { 0, 128, 255 })
            {
                byte[] pixels = new byte[w * h * 4];
                Array.Fill(pixels, fill);
                var result = PictureFilter.Apply(new PdfBitmap(w, h, w * 4, pixels), new PictureFilterOptions(look, 100, 100, 100, 100, true));
                Assert.Equal(pixels.Length, result.Pixels.Length);
            }
        }
    }

    // ================================================================= in a PDF

    [Theory]
    [InlineData(PictureLook.Document)]
    [InlineData(PictureLook.BlackAndWhite)]
    [InlineData(PictureLook.Grey)]
    public async Task A_filtered_scan_opens_and_really_changed(PictureLook look)
    {
        string input = await ScanPdfAsync(Scan());
        string output = NewPath;
        var result = await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(look), null, output);
        Assert.Equal(1, result.Changed);

        await using var doc = await Engine.OpenAsync(output);
        var page = await doc.RenderPageAsync(0, 400, 500);
        var right = Average(page, 380, 200, 398, 300);
        if (look == PictureLook.Grey)
        {
            Assert.True(Math.Abs(right.R - right.B) < 2, $"the margin kept colour: {right}");
        }
        else
        {
            Assert.True(right.G > 235, $"the shadowed margin came out {right}");
        }
    }

    [Fact]
    public async Task Black_and_white_is_stored_without_haze()
    {
        string input = await ScanPdfAsync(Scan());
        string output = NewPath;
        await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(PictureLook.BlackAndWhite), null, output);
        await using var doc = await Engine.OpenAsync(output);
        var pictures = await doc.GetPageImagesAsync(0);
        var picture = Assert.Single(pictures);
        for (int i = 0; i < picture.Pixels.Length; i += 4)
        {
            Assert.True(picture.Pixels[i] is 0 or 255, $"found grey {picture.Pixels[i]}");
        }
    }

    [Fact]
    public async Task Only_the_chosen_pages_change()
    {
        string input = NewPath;
        var first = Scan();
        var second = Scan(tilt: 1); // different pictures, so no sharing is involved
        await Engine.ImagesToPdfAsync(
            [new PdfImagePage(400, 500, () => Task.FromResult(new PdfImageData(null, first))), new PdfImagePage(400, 500, () => Task.FromResult(new PdfImageData(null, second)))],
            new ImagePageOptions(),
            input);
        string output = NewPath;
        var result = await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(PictureLook.Grey), [1], output);
        Assert.Equal(1, result.Changed);

        await using var doc = await Engine.OpenAsync(output);
        var kept = await doc.RenderPageAsync(0, 100, 125);
        var changed = await doc.RenderPageAsync(1, 100, 125);
        var keptPaper = Average(kept, 2, 50, 8, 75);
        var changedPaper = Average(changed, 2, 50, 8, 75);
        Assert.True(keptPaper.R - keptPaper.B > 20, $"page 1 kept its colour: {keptPaper}");
        Assert.True(Math.Abs(changedPaper.R - changedPaper.B) < 3, $"page 2 went grey: {changedPaper}");
    }

    [Fact]
    public async Task Words_on_the_page_are_untouched()
    {
        string input = _files.Create(2, "Hello filter");
        string output = NewPath;
        var result = await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(PictureLook.Document), null, output);

        // No pictures: nothing to do, and no file made.
        Assert.Equal(0, result.Changed);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task See_through_pictures_are_left_alone()
    {
        var picture = Scan(200, 200);
        for (int i = 3; i < picture.Pixels.Length; i += 4)
        {
            picture.Pixels[i] = 100;
        }

        string input = await ScanPdfAsync(picture);
        string output = NewPath;
        var result = await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(PictureLook.Grey), null, output);
        Assert.Equal(0, result.Changed);
        Assert.Equal(1, result.LeftAlone);
    }

    [Fact]
    public async Task The_preview_changes_nothing_and_shows_the_filter()
    {
        string input = await ScanPdfAsync(Scan());
        byte[] before = await File.ReadAllBytesAsync(input);
        var preview = await Engine.PreviewPictureFilterAsync(new PdfInputFile(input, null), 0, new PictureFilterOptions(PictureLook.Grey), 200, 250);
        var paper = Average(preview, 2, 100, 10, 150);
        Assert.True(Math.Abs(paper.R - paper.B) < 3, $"preview paper {paper}");
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task A_filter_keeps_a_password()
    {
        string plain = await ScanPdfAsync(Scan(300, 300));
        string locked = NewPath;
        await Engine.ProtectAsync(new PdfInputFile(plain, null), new PdfProtection("open-me"), locked);
        string output = NewPath;
        await Engine.FilterPicturesAsync(new PdfInputFile(locked, "open-me"), new PictureFilterOptions(PictureLook.Grey), null, output);
        await Assert.ThrowsAnyAsync<PdfOpenException>(() => Engine.OpenAsync(output));
        await using var doc = await Engine.OpenAsync(output, "open-me");
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task A_picture_on_many_pages_stays_one_picture_after_a_filter()
    {
        var scan = Scan(500, 600);
        string input = await ScanPdfAsync(scan, pages: 6);
        string output = NewPath;
        await Engine.FilterPicturesAsync(new PdfInputFile(input, null), new PictureFilterOptions(PictureLook.Document), null, output);

        string single = NewPath;
        string one = await ScanPdfAsync(scan);
        await Engine.FilterPicturesAsync(new PdfInputFile(one, null), new PictureFilterOptions(PictureLook.Document), null, single);
        Assert.True(new FileInfo(output).Length < new FileInfo(single).Length * 1.5, $"{new FileInfo(output).Length} against {new FileInfo(single).Length} for one page");
    }

    // ================================================================= other limits fixed alongside

    [Fact]
    public async Task Invisible_words_in_any_alphabet_can_be_found_and_add_almost_nothing()
    {
        string input = await ScanPdfAsync(Scan(300, 300));
        string output = NewPath;
        string[] words = ["\u6771\u4eac\u90fd", "\u041c\u043e\u0441\u043a\u0432\u0430", "\uc11c\uc6b8", "\u0391\u03b8\u03ae\u03bd\u03b1"];
        var ocr = new OcrPage(0, words.Select((w, i) => new OcrWord(w, new NormalizedRect(0.1, 0.1 + (i * 0.1), 0.3, 0.05))).ToArray());
        await Engine.AddTextLayerAsync(new PdfInputFile(input, null), [ocr], output);

        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length + (64 * 1024), "the blank font should be tiny");
        await using var doc = await Engine.OpenAsync(output);
        var found = await doc.GetPageWordsAsync(0);
        foreach (string word in words)
        {
            Assert.Contains(found, w => w.Text == word);
        }
    }

    [Fact]
    public void The_blank_font_is_a_real_font_file()
    {
        // PAPERY_KEEP_BLANK_FONT saves it, so a font tool such as fontTools can check it as well.
        byte[] font = BlankFont.Build();
        string keep = Environment.GetEnvironmentVariable("PAPERY_KEEP_BLANK_FONT") ?? string.Empty;
        if (keep.Length > 0)
        {
            File.WriteAllBytes(keep, font);
        }

        Assert.InRange(font.Length, 200, 2048);
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, font[..4]);
    }

    [Fact]
    public async Task Bookmarks_keep_their_spot_and_web_addresses()
    {
        string input = _files.Create(3);
        string output = NewPath;
        IReadOnlyList<PdfOutlineItem> outline =
        [
            new("Half way down", 1, [], Top: 0.5),
            new("Our site", null, [], Uri: "https://example.com/papery"),
        ];
        await Engine.SetOutlineAsync(new PdfInputFile(input, null), outline, output);

        await using var doc = await Engine.OpenAsync(output);
        var back = await doc.GetOutlineAsync();
        Assert.Equal(1, back[0].PageIndex);
        Assert.InRange(back[0].Top ?? -1, 0.45, 0.55);
        Assert.Equal("https://example.com/papery", back[1].Uri);
        Assert.Null(back[1].PageIndex);

        // And through the list editor, as the dialog uses it.
        var edited = new OutlineEditor(back);
        edited.Rename(0, "Renamed");
        var tree = edited.ToTree();
        Assert.InRange(tree[0].Top ?? -1, 0.45, 0.55);
        Assert.Equal("https://example.com/papery", tree[1].Uri);
    }

    [Fact]
    public async Task A_picture_watermark_is_stored_once()
    {
        string one = _files.Create(1, name: "one.pdf"), many = _files.Create(20, name: "many.pdf");
        var random = new Random(9);
        byte[] pixels = new byte[300 * 300 * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var stamp = new PdfPageStamp { Picture = new PdfBitmap(300, 300, 1200, pixels) };
        string smallOut = NewPath, bigOut = NewPath;
        await Engine.StampPagesAsync(new PdfInputFile(one, null), stamp, smallOut);
        await Engine.StampPagesAsync(new PdfInputFile(many, null), stamp, bigOut);
        long perFile = new FileInfo(smallOut).Length;
        Assert.True(new FileInfo(bigOut).Length < perFile * 3, $"{new FileInfo(bigOut).Length} for 20 pages against {perFile} for one");
    }

    [Fact]
    public async Task Redacting_a_locked_file_keeps_it_locked_and_removes_the_words()
    {
        string plain = _files.Create(2, "secret-word here");
        string locked = NewPath;
        await Engine.ProtectAsync(new PdfInputFile(plain, null), new PdfProtection("open-me"), locked);
        string output = NewPath;
        await Engine.RedactAsync(new PdfInputFile(locked, "open-me"), [new RedactionArea(0, new NormalizedRect(0, 0, 1, 1))], output);

        await Assert.ThrowsAnyAsync<PdfOpenException>(() => Engine.OpenAsync(output));
        await using var doc = await Engine.OpenAsync(output, "open-me");
        Assert.Empty(await doc.GetPageWordsAsync(0));
        Assert.NotEmpty(await doc.GetPageWordsAsync(1));
    }

    private static PdfWord Word(string text, double x, double y, double width = 0.06, double height = 0.015) =>
        new(text, new NormalizedRect(x, y, width, height), 0, text.Length, 11, false, false, "Arial", new PdfColor(0, 0, 0));

    [Fact]
    public void Lines_that_line_up_become_a_table()
    {
        var words = new List<PdfWord> { Word("Heading", 0.1, 0.05, 0.1) };
        string[] names = ["Apples", "Pears", "Plums"];
        for (int i = 0; i < names.Length; i++)
        {
            double y = 0.1 + (i * 0.02);
            words.Add(Word(names[i], 0.1, y));
            words.Add(Word("12", 0.4, y));
            words.Add(Word("9.50", 0.7, y));
        }

        words.Add(Word("After", 0.1, 0.3, 0.1));
        var blocks = DocxLayout.Blocks(words);
        Assert.Equal(3, blocks.Count);
        var table = blocks[1].Block.Table;
        Assert.NotNull(table);
        Assert.Equal(3, table!.Count);
        Assert.Equal(3, table[0].Count);
        Assert.Contains("Pears", table[1][0][0].Text);
        Assert.Contains("9.50", table[2][2][0].Text);
    }

    [Fact]
    public void One_line_with_a_gap_is_not_a_table()
    {
        var blocks = DocxLayout.Blocks([Word("Name", 0.1, 0.1), Word("Date", 0.7, 0.1), Word("Plain", 0.1, 0.2), Word("text", 0.17, 0.2)]);
        Assert.All(blocks, b => Assert.Null(b.Block.Table));
    }

    [Fact]
    public void Two_text_columns_are_read_one_after_the_other()
    {
        var words = new List<PdfWord>();
        for (int line = 0; line < 12; line++)
        {
            double y = 0.1 + (line * 0.02);
            for (int w = 0; w < 5; w++)
            {
                words.Add(Word($"L{line}w{w}", 0.05 + (w * 0.08), y));
                words.Add(Word($"R{line}w{w}", 0.55 + (w * 0.08), y));
            }
        }

        var blocks = DocxLayout.Blocks(words);
        string all = string.Concat(blocks.SelectMany(b => b.Block.Runs).Select(r => r.Text));
        Assert.True(all.IndexOf("L11w4", StringComparison.Ordinal) < all.IndexOf("R0w0", StringComparison.Ordinal), "the left column should finish before the right one starts");
        Assert.All(blocks, b => Assert.Null(b.Block.Table));
    }

    // ================================================================= merging copies

    [Fact]
    public void Merging_identical_pictures_shrinks_the_file_and_it_still_opens()
    {
        // Built by hand so each page really does hold its own copy of the picture.
        string input = NewPath;
        var random = new Random(5);
        byte[] data = new byte[30_000];
        random.NextBytes(data);
        using (var stream = File.Create(input))
        {
            var doc = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < 4; i++)
            {
                var page = doc.AddPage();
                var image = new PdfSharp.Pdf.PdfDictionary(doc);
                doc.Internals.AddObject(image);
                image.Elements["/Type"] = new PdfSharp.Pdf.PdfName("/XObject");
                image.Elements["/Subtype"] = new PdfSharp.Pdf.PdfName("/Image");
                image.Elements["/Width"] = new PdfSharp.Pdf.PdfInteger(100);
                image.Elements["/Height"] = new PdfSharp.Pdf.PdfInteger(100);
                image.Elements["/BitsPerComponent"] = new PdfSharp.Pdf.PdfInteger(8);
                image.Elements["/ColorSpace"] = new PdfSharp.Pdf.PdfName("/DeviceRGB");
                image.CreateStream(data);
                var xobjects = new PdfSharp.Pdf.PdfDictionary(doc);
                xobjects.Elements["/I"] = image.Reference;
                var resources = new PdfSharp.Pdf.PdfDictionary(doc);
                resources.Elements["/XObject"] = xobjects;
                page.Elements["/Resources"] = resources;
            }

            doc.Save(stream);
        }

        string output = NewPath;
        Assert.True(PdfDeduper.TryMerge(input, output));
        Assert.True(new FileInfo(output).Length < 45_000, $"{new FileInfo(input).Length} became {new FileInfo(output).Length}");
        using var again = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(4, again.PageCount);
    }

    [Fact]
    public void Nothing_to_merge_means_no_new_file_is_used()
    {
        string input = _files.Create(1, "One page");
        string output = NewPath;
        Assert.False(PdfDeduper.TryMerge(input, output));
    }
}
