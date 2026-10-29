using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfComposeTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private static async Task<string[]> PageTexts(string path, string? password = null)
    {
        await using var doc = await Engine.OpenAsync(path, password);
        var texts = new string[doc.PageCount];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = (await doc.GetPageTextAsync(i)).Trim();
        }

        return texts;
    }

    private string[] TempFiles() => Directory.GetFiles(_files.Folder, "*.papery-tmp*");

    [Fact]
    public async Task Organise_in_place_reorders_deletes_rotates_and_adds_blank()
    {
        string path = _files.Create(5, "Item");
        var request = new PdfComposeRequest(
            [new PdfInputFile(path)],
            [new PageRef(0, 4), new PageRef(0, 0, QuarterTurns: 1), PageRef.Blank(new PdfPageSize(300, 400)), new PageRef(0, 2)],
            EditInPlaceOf: 0);

        var result = await Engine.ComposeAsync(request, path);

        Assert.Equal(4, result.PageCount);
        Assert.Equal(["Item 5", "Item 1", "", "Item 3"], await PageTexts(path));
        await using var doc = await Engine.OpenAsync(path);
        Assert.True(doc.PageSizes[1].Width > doc.PageSizes[1].Height, "a quarter turn makes a portrait page landscape");
        Assert.Equal(300, doc.PageSizes[2].Width, 0.5f);
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Organise_keeps_bookmarks_and_metadata_of_the_edited_file()
    {
        string path = _files.CreateNavigable();
        var request = new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 0), new PageRef(0, 1), new PageRef(0, 2, 2)], EditInPlaceOf: 0);

        await Engine.ComposeAsync(request, path);

        await using var doc = await Engine.OpenAsync(path);
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(["Chapter One", "Chapter Two"], outline.Select(o => o.Title));
        Assert.Equal("Navigation test", (await doc.GetMetadataAsync()).Title);
    }

    [Fact]
    public async Task Deleted_page_content_is_really_gone_from_the_file()
    {
        string path = _files.CreateWithHeavyMiddlePage();
        long before = new FileInfo(path).Length;
        var request = new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 0), new PageRef(0, 2)], EditInPlaceOf: 0);

        await Engine.ComposeAsync(request, path);

        long after = new FileInfo(path).Length;
        Assert.True(after < before / 3, $"expected the heavy page's data to be removed ({before} -> {after} bytes)");
    }

    [Fact]
    public async Task Password_protected_file_keeps_its_password_after_organising()
    {
        string path = _files.Create(3, "Secret", password: "pw");
        var request = new PdfComposeRequest([new PdfInputFile(path, "pw")], [new PageRef(0, 2), new PageRef(0, 0)], EditInPlaceOf: 0);

        await Engine.ComposeAsync(request, path);

        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(path));
        Assert.Equal(PdfOpenError.PasswordRequired, ex.Error);
        Assert.Equal(["Secret 3", "Secret 1"], await PageTexts(path, "pw"));
    }

    [Fact]
    public async Task Can_replace_a_file_that_is_open_in_the_viewer()
    {
        string path = _files.Create(3, "Open");
        await using var viewer = await Engine.OpenAsync(path);
        await viewer.GetPageTextAsync(2); // make sure it is actively reading

        var request = new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 1)], EditInPlaceOf: 0);
        await Engine.ComposeAsync(request, path);

        Assert.Equal(["Open 2"], await PageTexts(path));
        // The old view keeps working until it is reloaded.
        Assert.Equal("Open 3", (await viewer.GetPageTextAsync(2)).Trim());
    }

    [Fact]
    public async Task Merge_builds_one_file_with_a_bookmark_per_input()
    {
        string a = _files.Create(2, "Alpha", name: "a.pdf");
        string b = _files.CreateNavigable("b.pdf");
        string output = Path.Combine(_files.Folder, "merged.pdf");
        IReadOnlyList<PdfOutlineItem> navOutline;
        await using (var nav = await Engine.OpenAsync(b))
        {
            navOutline = await nav.GetOutlineAsync();
        }

        var request = new PdfComposeRequest(
            [new PdfInputFile(a), new PdfInputFile(b)],
            [new PageRef(0, 0), new PageRef(0, 1), new PageRef(1, 0), new PageRef(1, 1), new PageRef(1, 2)],
            Outline:
            [
                new PdfOutlineItem("a", 0, []),
                new PdfOutlineItem("b", 2, navOutline.Select(o => Shift(o, 2)).ToArray()),
            ],
            Title: "Merged");

        var result = await Engine.ComposeAsync(request, output);

        Assert.False(result.BookmarksSkipped);
        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(5, doc.PageCount);
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(["a", "b"], outline.Select(o => o.Title));
        Assert.Equal(2, outline[1].PageIndex);
        Assert.Equal("Chapter One", outline[1].Children[0].Title);
        Assert.Equal(3, outline[1].Children[0].Children[0].PageIndex); // Section 1.1 was page 2 of b
        Assert.Equal("Merged", (await doc.GetMetadataAsync()).Title);
        Assert.Equal("Page 3 lighthouse", (await doc.GetPageTextAsync(4)).Trim());

        static PdfOutlineItem Shift(PdfOutlineItem item, int by) =>
            item with { PageIndex = item.PageIndex + by, Children = item.Children.Select(c => Shift(c, by)).ToArray() };
    }

    [Fact]
    public async Task New_file_from_a_protected_input_has_no_password()
    {
        string a = _files.Create(1, "Locked", password: "pw", name: "locked.pdf");
        string output = Path.Combine(_files.Folder, "out.pdf");

        await Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(a, "pw")], [new PageRef(0, 0)]), output);

        Assert.Equal(["Locked 1"], await PageTexts(output));
    }

    [Fact]
    public async Task Restricted_file_refuses_changes_and_is_left_untouched()
    {
        string path = _files.CreateRestricted();
        byte[] before = await File.ReadAllBytesAsync(path);

        var ex = await Assert.ThrowsAsync<PdfEditException>(() =>
            Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 1)], EditInPlaceOf: 0), path));

        Assert.Equal(PdfEditError.NotAllowed, ex.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Cancelling_leaves_the_original_untouched_and_no_temp_files()
    {
        string path = _files.Create(4, "Keep");
        byte[] before = await File.ReadAllBytesAsync(path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 3)], EditInPlaceOf: 0), path, null, cts.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Out_of_range_page_is_rejected_before_anything_is_written()
    {
        string path = _files.Create(2);
        string output = Path.Combine(_files.Folder, "x.pdf");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 7)]), output));

        Assert.False(File.Exists(output));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Unique_names_count_up()
    {
        string path = _files.Create(1, name: "part.pdf");
        Assert.EndsWith("part (2).pdf", SafeFile.MakeUnique(path));
    }
}
