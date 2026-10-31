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
