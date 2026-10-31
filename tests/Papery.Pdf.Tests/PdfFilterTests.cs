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
    public void Dust_goes_but_full_stops_stay()
    {
        const int Side = 2500; // about a 300 dpi page
        byte[] pixels = new byte[Side * Side * 4];
        Array.Fill(pixels, (byte)235);
        void Blob(int left, int top, int size)
        {
            for (int y = top; y < top + size; y++)
            {
                for (int x = left; x < left + size; x++)
                {
                    int at = ((y * Side) + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = 20;
                }
            }
        }

        Blob(500, 500, 3); // tiny dust: 9 pixels
        Blob(1500, 1500, 6); // a larger speck, alone on the paper: 36 pixels
        Blob(900, 900, 6); // a full stop, the same size...
        Blob(860, 880, 30); // ...right after a letter
        var result = PictureFilter.Apply(new PdfBitmap(Side, Side, Side * 4, pixels), new PictureFilterOptions(PictureLook.Document));
        Assert.Equal(255, result.Pixels[(((501 * Side) + 501) * 4) + 1]);
        Assert.Equal(255, result.Pixels[(((1502 * Side) + 1502) * 4) + 1]);
        Assert.True(result.Pixels[(((902 * Side) + 902) * 4) + 1] < 100, "the full stop must survive");
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
    public void A_table_becomes_well_formed_word_xml()
    {
        var words = new List<PdfWord>();
        for (int i = 0; i < 2; i++)
        {
            words.Add(Word("A & B", 0.1, 0.1 + (i * 0.02)));
            words.Add(Word("2", 0.5, 0.1 + (i * 0.02)));
        }

        var body = new System.Text.StringBuilder();
        DocxWriter.AppendBlock(body, DocxLayout.Blocks(words)[0].Block, [], true, new PdfPageSize(612, 792));
        var xml = System.Xml.Linq.XDocument.Parse($"""<body xmlns:w="urn:w">{body}</body>""");
        System.Xml.Linq.XNamespace w = "urn:w";
        Assert.Equal(2, xml.Descendants(w + "tr").Count());
        Assert.Equal(4, xml.Descendants(w + "tc").Count());
        Assert.Equal(2, xml.Descendants(w + "gridCol").Count());
        Assert.Single(xml.Descendants(w + "br"));
        Assert.All(xml.Descendants(w + "tc"), c => Assert.NotNull(c.Element(w + "p")));
        Assert.Contains("A & B", xml.Descendants(w + "t").Select(t => t.Value));
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

    [Theory]
    [InlineData(255)]
    [InlineData(120)]
    public async Task A_picture_saved_earlier_can_be_read_back_to_turn_it(byte alpha)
    {
        string input = _files.Create(1, name: $"pic{alpha}.pdf");
        string saved = NewPath;
        byte[] pixels = new byte[200 * 100 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 40;
            pixels[i + 2] = 40;
            pixels[i + 3] = alpha;
        }

        string name;
        await using (var doc = await Engine.OpenAsync(input))
        {
            var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Image, new PdfColor(0, 0, 0), [new NormalizedRect(0.1, 0.1, 0.4, 0.2)], Picture: new PdfBitmap(200, 100, 800, pixels)));
            name = added.Name;
            await doc.SaveAsync(saved);
        }

        await using var again = await Engine.OpenAsync(saved);
        var before = await again.GetAnnotationDraftAsync(0, name);
        Assert.Null(before?.Picture);
        var draft = await again.GetPictureDraftAsync(0, name);
        Assert.NotNull(draft?.Picture);
        Assert.Equal(PdfAnnotationKind.Image, draft!.Kind);

        // Turning it works and leaves a picture behind.
        await again.ChangeAnnotationAsync(0, name, draft with { RotationDegrees = 15 });
        var afterTurn = await again.GetPictureDraftAsync(0, name);
        Assert.Equal(15, afterTurn!.RotationDegrees);

        if (alpha < 255)
        {
            byte seen = draft.Picture!.Pixels[(draft.Picture.Stride * (draft.Picture.Height / 2)) + ((draft.Picture.Width / 2) * 4) + 3];
            Assert.InRange((int)seen, 80, 160);
        }
    }

    private async Task<(NormalizedRect Line, string Text)> LineOf(string path)
    {
        await using var doc = await Engine.OpenAsync(path);
        var words = await doc.GetPageWordsAsync(0);
        double left = words.Min(w => w.Area.X), top = words.Min(w => w.Area.Y), right = words.Max(w => w.Area.Right), bottom = words.Max(w => w.Area.Bottom);
        return (new NormalizedRect(left, top, right - left, bottom - top), string.Join(' ', words.Select(w => w.Text)));
    }

    [Fact]
    public async Task Words_with_letters_the_font_has_change_in_place()
    {
        string input = _files.Create(1, "Hello Papery", name: "inplace.pdf");
        var (line, text) = await LineOf(input);
        string output = NewPath;
        Assert.True(await Engine.TryReplaceLineAsync(new PdfInputFile(input, null), 0, line, text, "Pale Help", output), "the letters are all there, so this should work");

        await using var doc = await Engine.OpenAsync(output);
        string now = await doc.GetPageTextAsync(0);
        Assert.Contains("Pale Help", now);
        Assert.DoesNotContain("Hello Papery", now);
    }

    [Fact]
    public async Task Words_with_missing_letters_are_refused_and_nothing_is_written()
    {
        string input = _files.Create(1, "Hello Papery", name: "missing.pdf");
        var (line, text) = await LineOf(input);
        string output = NewPath;
        Assert.False(await Engine.TryReplaceLineAsync(new PdfInputFile(input, null), 0, line, text, "Zebra XQ", output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Pictures_inside_page_fragments_are_found()
    {
        string input = _files.Create(1, name: "fragment.pdf");
        string stamped = NewPath;
        byte[] pixels = new byte[80 * 80 * 4];
        Array.Fill(pixels, (byte)255);
        await Engine.StampPagesAsync(new PdfInputFile(input, null), new PdfPageStamp { Picture = new PdfBitmap(80, 80, 320, pixels), Opacity = 1 }, stamped);

        // Importing pages as a fragment is what "put several pages on a sheet" does; here it's enough that top-level pictures still come back.
        await using var doc = await Engine.OpenAsync(stamped);
        Assert.Single(await doc.GetPageImagesAsync(0));
    }

    /// <summary>Real-world shapes of PDF, from the repo's test set: nothing may break, and every page must survive.</summary>
    [Fact]
    public async Task Every_ordinary_test_file_survives_filters_and_compress()
    {
        const long Largest = 40 * 1024 * 1024;
        string? folder = AppContext.BaseDirectory;
        while (folder != null && !Directory.Exists(Path.Combine(folder, "PaperyTestFiles")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        if (folder == null)
        {
            return; // the set isn't there, as on a machine with only the code
        }

        var checkedFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(folder, "PaperyTestFiles"), "*.pdf"))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("damaged", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase) || new FileInfo(file).Length > Largest)
            {
                continue;
            }

            PdfDocument original;
            try
            {
                original = await Engine.OpenAsync(file);
            }
            catch (PdfOpenException)
            {
                continue; // locked or broken on purpose
            }

            int pages = original.PageCount;
            await original.DisposeAsync();

            string filtered = NewPath, smaller = NewPath;
            var result = await Engine.FilterPicturesAsync(new PdfInputFile(file, null), new PictureFilterOptions(PictureLook.Document, Sharpen: 20), null, filtered);
            if (result.Changed > 0)
            {
                await using var doc = await Engine.OpenAsync(filtered);
                Assert.Equal(pages, doc.PageCount);
                _ = await doc.RenderPageAsync(0, 100, 100);
            }

            var compressed = await Engine.CompressAsync(new PdfInputFile(file, null), CompressOptions.Balanced, smaller);
            if (compressed.Changed)
            {
                await using var doc = await Engine.OpenAsync(smaller);
                Assert.Equal(pages, doc.PageCount);
            }

            checkedFiles.Add(name);
        }

        Assert.NotEmpty(checkedFiles);
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
