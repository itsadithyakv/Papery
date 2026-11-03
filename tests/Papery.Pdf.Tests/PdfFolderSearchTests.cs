namespace Papery.Pdf.Tests;

public sealed class PdfFolderSearchTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private async Task<List<FolderSearchHit>> SearchAsync(string query, IProgress<FolderSearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var hits = new List<FolderSearchHit>();
        await foreach (var hit in Engine.SearchFolderAsync(_files.Folder, query, progress, cancellationToken))
        {
            hits.Add(hit);
        }

        return hits;
    }

    [Fact]
    public async Task Words_are_found_on_every_page_of_every_file_including_subfolders()
    {
        _files.Create(2, "A quiet lighthouse", name: "one.pdf");
        Directory.CreateDirectory(Path.Combine(_files.Folder, "inner"));
        _files.Create(1, "Another LIGHTHOUSE story", name: Path.Combine("inner", "two.pdf"));
        _files.Create(1, "Nothing to see", name: "three.pdf");

        var hits = await SearchAsync("lighthouse");

        Assert.Equal(3, hits.Count);
        Assert.Equal([0, 1], hits.Where(h => h.Path.EndsWith("one.pdf", StringComparison.Ordinal)).Select(h => h.PageIndex));
        Assert.Contains(hits, h => h.Path.EndsWith("two.pdf", StringComparison.Ordinal) && h.Match == "LIGHTHOUSE");
    }

    [Fact]
    public async Task A_snippet_shows_the_words_around_the_match()
    {
        _files.Create(1, "Paper has outlived every prediction", name: "one.pdf");

        var hit = Assert.Single(await SearchAsync("outlived every"));

        Assert.Equal("outlived every", hit.Match);
        Assert.EndsWith("has ", hit.Before, StringComparison.Ordinal);
        Assert.StartsWith(" prediction", hit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_file_is_counted_not_searched()
    {
        _files.Create(1, "A quiet lighthouse", name: "open.pdf");
        _files.Create(1, "A secret lighthouse", password: "pw", name: "locked.pdf");
        FolderSearchProgress? last = null;
        var progress = new SynchronousProgress(p => last = p);

        var hits = await SearchAsync("lighthouse", progress);

        Assert.Single(hits);
        Assert.Equal(1, last?.Locked);
        Assert.Equal(2, last?.FilesTotal);
    }

    [Fact]
    public async Task Stopping_a_search_stops_it()
    {
        for (int i = 0; i < 5; i++)
        {
            _files.Create(1, "lighthouse", name: $"f{i}.pdf");
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchAsync("lighthouse", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task An_empty_query_finds_nothing()
    {
        _files.Create(1, "lighthouse", name: "one.pdf");

        Assert.Empty(await SearchAsync("   "));
    }

    private sealed class SynchronousProgress(Action<FolderSearchProgress> report) : IProgress<FolderSearchProgress>
    {
        public void Report(FolderSearchProgress value) => report(value);
    }
}
