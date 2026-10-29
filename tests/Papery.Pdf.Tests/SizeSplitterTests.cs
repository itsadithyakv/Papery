using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class SizeSplitterTests : IDisposable
{
    private readonly TestPdf _files = new();

    public void Dispose() => _files.Dispose();

    private async Task<List<string>> SplitAsync(string path, int pageCount, long limit)
    {
        var written = new List<string>();
        var source = new PdfInputFile(path);
        await SizeSplitter.SplitAsync(
            PdfEngine.Shared,
            pageCount,
            limit,
            new FileInfo(path).Length,
            pages => new PdfComposeRequest([source], pages.Select(p => new PageRef(0, p)).ToArray()),
            part => Path.Combine(_files.Folder, $"out {part.Name}.pdf"),
            written,
            null,
            CancellationToken.None);
        return written;
    }

    private static async Task<string[]> Texts(string path)
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(path);
        var texts = new string[doc.PageCount];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = (await doc.GetPageTextAsync(i)).Trim();
        }

        return texts;
    }

    [Fact]
    public async Task A_generous_limit_makes_one_file()
    {
        string path = _files.Create(5, "Small");

        var written = await SplitAsync(path, 5, 50L * 1024 * 1024);

        var only = Assert.Single(written);
        Assert.Equal(5, (await Texts(only)).Length);
    }

    [Fact]
    public async Task Every_file_fits_unless_one_page_alone_is_too_big_and_nothing_is_lost()
    {
        // Page 2 alone is far bigger than the limit; pages 1 and 3 are small.
        string path = _files.CreateWithHeavyMiddlePage();
        long limit = 40 * 1024;

        var written = await SplitAsync(path, 3, limit);

        var pages = new List<string>();
        foreach (string file in written)
        {
            var texts = await Texts(file);
            Assert.True(new FileInfo(file).Length <= limit || texts.Length == 1, $"{Path.GetFileName(file)} is too big with {texts.Length} pages");
            pages.AddRange(texts.Select(t => t.Split('\n')[0].Trim()));
        }

        Assert.Equal(["Page 1", "Page 2", "Page 3"], pages);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "*.papery-tmp*"));
    }

    [Fact]
    public async Task Parts_are_as_large_as_the_limit_allows()
    {
        string path = _files.Create(12, "Even");
        long onePage = await SizeOf(path, [0]);
        long sixPages = await SizeOf(path, [0, 1, 2, 3, 4, 5]);

        // Shared fonts make the first page expensive and the rest cheap: measure what one more page adds.
        long perPage = (sixPages - onePage) / 5;

        // Room for six pages but not seven: expect two files of six.
        var written = await SplitAsync(path, 12, sixPages + (perPage / 2));

        Assert.Equal([6, 6], (await Task.WhenAll(written.Select(Texts))).Select(t => t.Length));
    }

    private async Task<long> SizeOf(string path, int[] pages)
    {
        string output = Path.Combine(_files.Folder, $"probe-{Guid.NewGuid():N}.pdf");
        await PdfEngine.Shared.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], pages.Select(p => new PageRef(0, p)).ToArray()), output);
        return new FileInfo(output).Length;
    }
}
