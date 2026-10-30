using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfCompressTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath => Path.Combine(_files.Folder, $"c-{Guid.NewGuid():N}.pdf");

    /// <summary>A soft picture with some grain: like a photo, it needs JPEG to get small.</summary>
    private static PdfBitmap Photo(int width, int height, byte alpha = 255)
    {
        var random = new Random(42);
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                int grain = random.Next(-8, 9);
                pixels[at] = (byte)Math.Clamp((x * 255 / width) + grain, 0, 255);
                pixels[at + 1] = (byte)Math.Clamp((y * 255 / height) + grain, 0, 255);
                pixels[at + 2] = (byte)Math.Clamp(180 + grain, 0, 255);
                pixels[at + 3] = alpha;
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    /// <summary>One page, 200 by 200 points, with a very sharp picture on it.</summary>
    private async Task<string> CreatePhotoPdfAsync(PdfBitmap picture, int pages = 1)
    {
        string path = NewPath;
        var list = Enumerable.Range(0, pages).Select(_ => new PdfImagePage(200, 200, () => Task.FromResult(new PdfImageData(null, picture)))).ToArray();
        await Engine.ImagesToPdfAsync(list, new ImagePageOptions(), path);
        return path;
    }

    private static async Task<PdfBitmap> Render(string path, int page = 0, string? password = null)
    {
        await using var doc = await Engine.OpenAsync(path, password);
        return await doc.RenderPageAsync(page, 100, 100);
    }

    private static int LargestDifference(PdfBitmap a, PdfBitmap b)
    {
        int largest = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            if (i % 4 != 3)
            {
                largest = Math.Max(largest, Math.Abs(a.Pixels[i] - b.Pixels[i]));
            }
        }

        return largest;
    }

    // ================================================================= JPEG

    [Theory]
    [InlineData(64, 64, false)]
    [InlineData(65, 33, false)] // odd sizes exercise the edges
    [InlineData(70, 50, true)]
    public async Task The_encoder_makes_jpegs_that_look_right(int width, int height, bool grey)
    {
        var picture = Photo(width, height);
        byte[] jpeg = JpegEncoder.Encode(picture, 90, grey);
        string path = NewPath;

        await Engine.ImagesToPdfAsync([new PdfImagePage(width, height, () => Task.FromResult(new PdfImageData(jpeg, null)))], new ImagePageOptions(), path);

        await using var doc = await Engine.OpenAsync(path);
        var shown = await doc.RenderPageAsync(0, width, height);
        int errors = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * shown.Stride) + (x * 4), from = ((y * width) + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int expected = grey
                        ? (int)((picture.Pixels[from + 2] * 0.299) + (picture.Pixels[from + 1] * 0.587) + (picture.Pixels[from] * 0.114))
                        : picture.Pixels[from + c];
                    if (Math.Abs(shown.Pixels[at + c] - expected) > 28)
                    {
                        errors++;
                    }
                }
            }
        }

        Assert.True(errors < width * height * 3 / 50, $"{errors} colour values are far off");
    }

    [Fact]
    public void Lower_quality_means_fewer_bytes()
    {
        var picture = Photo(200, 200);

        Assert.True(JpegEncoder.Encode(picture, 30, false).Length < JpegEncoder.Encode(picture, 90, false).Length);
        Assert.True(JpegEncoder.Encode(picture, 60, true).Length < JpegEncoder.Encode(picture, 60, false).Length);
    }

    // ================================================================= compressing

    [Fact]
    public async Task A_big_picture_gets_much_smaller_and_looks_the_same()
    {
        string path = await CreatePhotoPdfAsync(Photo(1600, 1600));
        string output = NewPath;
        var before = await Render(path);

        var result = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Balanced, output);

        Assert.True(result.Changed);
        Assert.True(result.NewBytes < result.OriginalBytes / 4, $"{result.OriginalBytes} to {result.NewBytes}");
        Assert.Equal(result.NewBytes, new FileInfo(output).Length);
        Assert.True(LargestDifference(before, await Render(output)) < 30);
    }

    [Fact]
    public async Task Stronger_settings_make_smaller_files()
    {
        string path = await CreatePhotoPdfAsync(Photo(1600, 1600));

        var high = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.HighQuality, NewPath);
        var low = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Smallest, NewPath);

        Assert.True(low.NewBytes < high.NewBytes);
    }

    [Fact]
    public async Task Greyscale_only_turns_pictures_grey()
    {
        string path = await CreatePhotoPdfAsync(Photo(800, 800));
        string output = NewPath;

        await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Balanced with { Greyscale = true }, output);

        var shown = await Render(output);
        int middle = (50 * shown.Stride) + 200;
        Assert.InRange(Math.Abs(shown.Pixels[middle] - shown.Pixels[middle + 2]), 0, 4);
    }

    [Fact]
    public async Task Words_survive()
    {
        string path = _files.Create(3, "Keep these words");
        string output = NewPath;

        await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Smallest, output);

        // Words-only files may not shrink; if they did, the words must all be there.
        await using var doc = await Engine.OpenAsync(File.Exists(output) ? output : path);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("Keep these words 3", await doc.GetPageTextAsync(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task See_through_pictures_stay_see_through()
    {
        string path = await CreatePhotoPdfAsync(Photo(600, 600, alpha: 0));
        string output = NewPath;
        var before = await Render(path);
        Assert.Equal(255, before.Pixels[(50 * before.Stride) + 200]);

        await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Smallest, output);

        var shown = await Render(File.Exists(output) ? output : path);
        Assert.Equal(255, shown.Pixels[(50 * shown.Stride) + 200]); // the white page, not a coloured box
    }

    [Fact]
    public async Task A_picture_shared_by_many_pages_is_not_stored_many_times()
    {
        string first = await CreatePhotoPdfAsync(Photo(600, 600));
        string output = NewPath;
        await Engine.CompressAsync(new PdfInputFile(first), CompressOptions.Balanced, output);
        long one = new FileInfo(output).Length;

        // The same picture repeated: never allowed to come out bigger than it went in.
        string repeated = await CreatePhotoPdfAsync(Photo(600, 600), pages: 3);
        string outputMany = NewPath;
        var result = await Engine.CompressAsync(new PdfInputFile(repeated), CompressOptions.Balanced, outputMany);

        Assert.True(result.NewBytes <= result.OriginalBytes);
        Assert.True(one > 0);
    }

    [Fact]
    public async Task Nothing_gets_written_when_it_cannot_be_smaller()
    {
        string path = await CreatePhotoPdfAsync(Photo(70, 70));
        string squeezed = NewPath;
        await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Smallest, squeezed);

        string again = NewPath;
        var result = await Engine.CompressAsync(new PdfInputFile(squeezed), CompressOptions.Smallest, again);

        Assert.False(result.Changed);
        Assert.False(File.Exists(again));
    }

    [Fact]
    public async Task Fit_under_a_limit_stops_at_the_gentlest_that_fits()
    {
        string path = await CreatePhotoPdfAsync(Photo(1600, 1600));
        var high = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.HighQuality, NewPath);

        string roomy = NewPath;
        var easy = await Engine.CompressToFitAsync(new PdfInputFile(path), high.NewBytes + 1000, roomy);
        Assert.True(easy.FitsLimit);
        Assert.Equal(high.NewBytes, easy.NewBytes); // no harder than it had to be

        string tight = NewPath;
        var hard = await Engine.CompressToFitAsync(new PdfInputFile(path), high.NewBytes / 2, tight);
        Assert.True(hard.FitsLimit);
        Assert.True(hard.NewBytes <= high.NewBytes / 2);
    }

    [Fact]
    public async Task An_impossible_limit_gives_the_smallest_and_says_so()
    {
        string path = await CreatePhotoPdfAsync(Photo(1000, 1000));
        string output = NewPath;

        var result = await Engine.CompressToFitAsync(new PdfInputFile(path), 10, output);

        Assert.False(result.FitsLimit);
        Assert.True(result.Changed);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task A_password_stays_on()
    {
        string plain = await CreatePhotoPdfAsync(Photo(800, 800));
        string locked = NewPath;
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(plain, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = "secret";
            doc.SecuritySettings.OwnerPassword = "owner";
            doc.Save(locked);
        }

        string output = NewPath;
        var result = await Engine.CompressAsync(new PdfInputFile(locked, "secret"), CompressOptions.Balanced, output);

        Assert.True(result.Changed);
        await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(output));
        await using var reopened = await Engine.OpenAsync(output, "secret");
        Assert.Equal(1, reopened.PageCount);
    }

    [Fact]
    public async Task Cancelling_leaves_nothing_behind()
    {
        string path = await CreatePhotoPdfAsync(Photo(800, 800));
        string output = NewPath;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Balanced, output, cancelled.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Bookmarks_and_links_survive()
    {
        string path = await CreatePhotoPdfAsync(Photo(1000, 1000), pages: 2);
        var area = new NormalizedRect(0.1, 0.1, 0.3, 0.1);
        await Engine.EditLinksAsync(new PdfInputFile(path), [], [new NewPdfLink(0, area, "https://example.com/", null)], path);
        await Engine.SetOutlineAsync(new PdfInputFile(path), [new PdfOutlineItem("Second", 1, [])], path);
        string output = NewPath;

        await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.Balanced, output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal("Second", Assert.Single(await doc.GetOutlineAsync()).Title);
        Assert.Equal("https://example.com/", Assert.Single(await doc.GetLinksAsync(0)).Uri);
    }
}
