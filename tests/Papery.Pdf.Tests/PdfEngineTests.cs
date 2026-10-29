using System.Text;

namespace Papery.Pdf.Tests;

public sealed class PdfEngineTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task Open_reports_page_count_and_sizes()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(pages: 3));

        Assert.Equal(3, doc.PageCount);
        Assert.All(doc.PageSizes, s =>
        {
            Assert.Equal(595.28f, s.Width, 0.5f);
            Assert.Equal(841.89f, s.Height, 0.5f);
        });
        Assert.False(doc.IsEncrypted);
        Assert.False(doc.WasRepaired);
    }

    [Fact]
    public async Task Render_draws_page_content_at_the_requested_size()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(pages: 1));

        var bitmap = await doc.RenderPageAsync(0, 300, 424);

        Assert.Equal(300, bitmap.Width);
        Assert.Equal(424, bitmap.Height);
        Assert.Equal(300 * 4 * 424, bitmap.Pixels.Length);
        // The page is white with black text, so some pixels must be dark.
        int dark = 0;
        for (int i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            if (bitmap.Pixels[i] < 100)
            {
                dark++;
            }
        }

        Assert.True(dark > 50, $"Expected rendered text, found only {dark} dark pixels.");
    }

    [Fact]
    public async Task Text_extraction_returns_the_text_on_each_page()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(pages: 2, text: "Lighthouse"));

        Assert.Contains("Lighthouse 1", await doc.GetPageTextAsync(0), StringComparison.Ordinal);
        Assert.Contains("Lighthouse 2", await doc.GetPageTextAsync(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_protected_file_needs_the_right_password()
    {
        string path = _files.Create(pages: 2, password: "s3cret");

        var missing = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(path));
        Assert.Equal(PdfOpenError.PasswordRequired, missing.Error);

        var wrong = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(path, "nope"));
        Assert.Equal(PdfOpenError.PasswordRequired, wrong.Error);

        await using var doc = await Engine.OpenAsync(path, "s3cret");
        Assert.Equal(2, doc.PageCount);
        Assert.True(doc.IsEncrypted);
        Assert.Equal(6, doc.SecurityRevision); // AES-256
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    [InlineData("header-then-garbage")]
    public async Task Broken_files_fail_with_a_clear_reason_instead_of_crashing(string kind)
    {
        byte[] bytes = kind switch
        {
            "empty" => [],
            "garbage" => Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray(),
            _ => [.. Encoding.ASCII.GetBytes("%PDF-1.7\n"), .. Enumerable.Range(0, 5000).Select(i => (byte)(i * 17))],
        };

        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(_files.CreateRaw($"{kind}.pdf", bytes)));
        Assert.Equal(PdfOpenError.Damaged, ex.Error);
    }

    [Fact]
    public async Task Missing_file_reports_file_unreadable()
    {
        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(Path.Combine(_files.Folder, "nope.pdf")));
        Assert.Equal(PdfOpenError.FileUnreadable, ex.Error);
    }

    [Fact]
    public async Task Open_file_is_not_locked_against_rename_or_reading()
    {
        // Saving later works by writing a new file and swapping it in, which needs this.
        string path = _files.Create(pages: 1);
        await using var doc = await Engine.OpenAsync(path);

        using (File.OpenRead(path))
        {
        }

        string moved = path + ".moved";
        File.Move(path, moved);
        Assert.True(File.Exists(moved));

        // Already-opened document keeps working from its handle.
        Assert.Contains("Hello Papery 1", await doc.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Many_threads_can_use_the_engine_at_once()
    {
        // PDFium itself is not thread-safe; PdfiumThread must serialise everything.
        var docs = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            Engine.OpenAsync(_files.Create(pages: 4, name: $"t{i}.pdf"))));
        try
        {
            var work = Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
            {
                var doc = docs[i % docs.Length];
                var bitmap = await doc.RenderPageAsync(i % 4, 120, 170);
                string text = await doc.GetPageTextAsync(i % 4);
                return bitmap.Width == 120 && text.Contains($"Hello Papery {(i % 4) + 1}", StringComparison.Ordinal);
            }));

            Assert.All(await Task.WhenAll(work), Assert.True);
        }
        finally
        {
            foreach (var doc in docs)
            {
                await doc.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Cancelled_requests_are_skipped()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(pages: 1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderPageAsync(0, 100, 100, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Bad_page_index_is_rejected_before_reaching_native_code()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(pages: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.RenderPageAsync(1, 100, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.GetPageTextAsync(-1));
    }
}
