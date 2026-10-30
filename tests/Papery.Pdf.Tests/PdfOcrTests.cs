using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfOcrTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    /// <summary>A "scan": pages that are only a picture, with no text in them.</summary>
    private async Task<string> CreateScanAsync(int pages = 2)
    {
        byte[] pixels = new byte[40 * 40 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }

        var picture = new PdfBitmap(40, 40, 160, pixels);
        string path = Path.Combine(_files.Folder, $"scan-{Guid.NewGuid():N}.pdf");
        var list = Enumerable.Range(0, pages).Select(_ => new PdfImagePage(600, 800, () => Task.FromResult(new PdfImageData(null, picture)))).ToArray();
        await Engine.ImagesToPdfAsync(list, new ImagePageOptions(), path);
        return path;
    }

    private static readonly OcrWord[] Sentence =
    [
        new("Hello", new NormalizedRect(0.1, 0.1, 0.15, 0.02)),
        new("there", new NormalizedRect(0.27, 0.1, 0.15, 0.02)),
        new("friend", new NormalizedRect(0.1, 0.14, 0.15, 0.02)),
    ];

    [Fact]
    public async Task Words_become_searchable_but_the_page_looks_the_same()
    {
        string path = await CreateScanAsync();
        PdfBitmap before;
        await using (var doc = await Engine.OpenAsync(path))
        {
            before = await doc.RenderPageAsync(0, 300, 400);
        }

        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, Sentence)], path);

        await using (var doc = await Engine.OpenAsync(path))
        {
            string text = await doc.GetPageTextAsync(0);
            Assert.Contains("Hello there", text, StringComparison.Ordinal);
            Assert.Contains("friend", text, StringComparison.Ordinal);
            Assert.Empty((await doc.GetPageTextAsync(1)).Trim());
            Assert.Single(await doc.FindOnPageAsync(0, "friend"));

            var after = await doc.RenderPageAsync(0, 300, 400);
            Assert.Equal(before.Pixels, after.Pixels);
        }
    }

    [Fact]
    public async Task Words_close_together_still_read_as_separate_words()
    {
        string path = await CreateScanAsync(1);
        OcrWord[] tight = [new("It", new NormalizedRect(0.1, 0.1, 0.03, 0.02)), new("mentions", new NormalizedRect(0.132, 0.1, 0.12, 0.02)), new("recipe", new NormalizedRect(0.254, 0.1, 0.09, 0.02))];

        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, tight)], path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.Contains("It mentions recipe", await doc.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_finds_a_word_where_it_is_in_the_picture()
    {
        string path = await CreateScanAsync(1);
        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, Sentence)], path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.True(await doc.GetCharIndexAtAsync(0, 0.17, 0.11) >= 0);
        Assert.True(await doc.GetCharIndexAtAsync(0, 0.8, 0.8) < 0);
    }

    [Fact]
    public async Task Words_outside_western_letters_are_kept()
    {
        string path = await CreateScanAsync(1);
        OcrWord[] words = [new("Привет", new NormalizedRect(0.1, 0.1, 0.2, 0.02)), new("Ελληνικά", new NormalizedRect(0.1, 0.2, 0.2, 0.02))];

        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, words)], path);

        await using var doc = await Engine.OpenAsync(path);
        string text = await doc.GetPageTextAsync(0);
        Assert.Contains("Привет", text, StringComparison.Ordinal);
        Assert.Contains("Ελληνικά", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_font_goes_in_once_however_many_words_use_it()
    {
        string one = await CreateScanAsync(1);
        string many = await CreateScanAsync(1);
        await Engine.AddTextLayerAsync(new PdfInputFile(one), [new OcrPage(0, [new OcrWord("Привет", new NormalizedRect(0.1, 0.1, 0.1, 0.02))])], one);
        var lots = Enumerable.Range(0, 200).Select(i => new OcrWord("Привет", new NormalizedRect(0.1, 0.002 + (i * 0.004), 0.1, 0.003))).ToArray();
        await Engine.AddTextLayerAsync(new PdfInputFile(many), [new OcrPage(0, lots)], many);

        // Two hundred copies of a whole font would be tens of megabytes.
        Assert.True(new FileInfo(many).Length < new FileInfo(one).Length + 200_000, $"{new FileInfo(many).Length} vs {new FileInfo(one).Length}");
    }

    [Fact]
    public async Task Live_text_is_selectable_at_once_and_the_file_stays_as_it_was()
    {
        string path = await CreateScanAsync(1);
        byte[] before = await File.ReadAllBytesAsync(path);

        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Empty((await doc.GetPageTextAsync(0)).Trim()); // reading first, so a stale text page would show up
            Assert.True(await doc.AddLiveTextAsync(new OcrPage(0, Sentence)));

            Assert.Contains("Hello there", await doc.GetPageTextAsync(0), StringComparison.Ordinal);
            Assert.False(doc.HasAnnotationChanges);

            // What the viewer uses to start a selection: the word must be found where it shows.
            int first = await doc.GetCharIndexAtAsync(0, 0.17, 0.11);
            Assert.True(first >= 0, $"no character under the word (got {first})");
            var selected = await doc.GetSelectionAsync(0, first, await doc.GetCharIndexAtAsync(0, 0.41, 0.11));
            Assert.Contains("there", selected.Text, StringComparison.Ordinal);
            Assert.Single(await doc.FindOnPageAsync(0, "friend"));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Words_land_right_on_a_turned_page()
    {
        string path = _files.Create(1, "x");
        var doc0 = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        doc0.Pages[0].Rotate = 90;
        doc0.Save(path);
        doc0.Dispose();

        await Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, [new OcrWord("Corner", new NormalizedRect(0.7, 0.8, 0.2, 0.05))])], path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.True(await doc.GetCharIndexAtAsync(0, 0.8, 0.82) >= 0);
    }

    [Fact]
    public async Task A_failure_leaves_the_file_alone()
    {
        string path = await CreateScanAsync(1);
        byte[] before = await File.ReadAllBytesAsync(path);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine.AddTextLayerAsync(new PdfInputFile(path), [new OcrPage(0, Sentence)], path, cancelled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }
}
