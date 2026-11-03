using System.Text;
using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfAttachmentTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath => Path.Combine(_files.Folder, $"a-{Guid.NewGuid():N}.pdf");

    private string Note(string name, string text)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_files.Folder, Guid.NewGuid().ToString("N"))).FullName;
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task Files_are_attached_and_read_back_whole()
    {
        string pdf = _files.Create(2);

        await Engine.ChangeAttachmentsAsync(new PdfInputFile(pdf), [Note("notes.txt", "first"), Note("figures.csv", "a,b\n1,2")], [], pdf);

        await using var doc = await Engine.OpenAsync(pdf);
        var attached = await doc.GetAttachmentsAsync();
        // A PDF keeps attachments sorted by name, whatever order they were added in.
        Assert.Equal(["figures.csv", "notes.txt"], attached.Select(a => a.Name));
        Assert.Equal("a,b\n1,2", Encoding.UTF8.GetString(await doc.ReadAttachmentAsync(attached[0].Index)).TrimStart('\uFEFF'));
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public async Task A_second_file_with_the_same_name_gets_a_number()
    {
        string pdf = _files.Create(1);
        await Engine.ChangeAttachmentsAsync(new PdfInputFile(pdf), [Note("notes.txt", "one")], [], pdf);

        await Engine.ChangeAttachmentsAsync(new PdfInputFile(pdf), [Note("notes.txt", "two")], [], pdf);

        await using var doc = await Engine.OpenAsync(pdf);
        Assert.Equal(["notes (2).txt", "notes.txt"], (await doc.GetAttachmentsAsync()).Select(a => a.Name));
    }

    [Fact]
    public async Task An_attachment_can_be_removed()
    {
        string pdf = _files.Create(1);
        await Engine.ChangeAttachmentsAsync(new PdfInputFile(pdf), [Note("keep.txt", "k"), Note("drop.txt", "d")], [], pdf);

        int drop;
        await using (var before = await Engine.OpenAsync(pdf))
        {
            drop = (await before.GetAttachmentsAsync()).Single(a => a.Name == "drop.txt").Index;
        }

        await Engine.ChangeAttachmentsAsync(new PdfInputFile(pdf), [], [drop], pdf);

        await using var doc = await Engine.OpenAsync(pdf);
        Assert.Equal(["keep.txt"], (await doc.GetAttachmentsAsync()).Select(a => a.Name));
    }

    [Fact]
    public async Task A_password_protected_file_stays_protected()
    {
        string locked = NewPath;
        await Engine.ProtectAsync(new PdfInputFile(_files.Create(1)), new PdfProtection("pw"), locked);

        await Engine.ChangeAttachmentsAsync(new PdfInputFile(locked, "pw"), [Note("notes.txt", "secret")], [], locked);

        await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(locked));
        await using var doc = await Engine.OpenAsync(locked, "pw");
        Assert.Single(await doc.GetAttachmentsAsync());
    }

    [Fact]
    public void Unique_names_keep_the_extension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt", "A (2).txt" };

        Assert.Equal("a (3).txt", PdfAttacher.Unique("a.txt", taken));
        Assert.Equal("b.txt", PdfAttacher.Unique("b.txt", taken));
    }
}
