using System.Globalization;
using System.Text;
using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class PdfSecurityTests : IDisposable
{
    private const string Secret = "SECRETWORD";
    private const string Open = "PUBLICWORD";

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath => Path.Combine(_files.Folder, $"s-{Guid.NewGuid():N}.pdf");

    /// <summary>A hand-written PDF, so we know exactly what's in it and how the words are stored.</summary>
    private string Handmade(IReadOnlyList<string> objects, string? trailerExtra = null)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int table = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{table}\n%%EOF\n");
        string path = NewPath;
        File.WriteAllText(path, text.ToString(), Encoding.Latin1);
        return path;
    }

    private static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Two pages. The first has the secret at the top and a public word at the bottom; both are plain in the file.</summary>
    private string CreateSecretPdf(string pageExtra = "", string catalogExtra = "", params string[] moreObjects)
    {
        string content = $"BT /F1 24 Tf 72 700 Td ({Secret}) Tj ET BT /F1 24 Tf 72 200 Td ({Open}) Tj ET";
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra}>>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R {pageExtra}>>",
            Stream(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            Stream($"BT /F1 24 Tf 72 700 Td ({Secret} on page two) Tj ET"),
        };
        objects.AddRange(moreObjects);
        return Handmade(objects);
    }

    // The top of page one, where the secret word is.
    private static readonly NormalizedRect SecretBox = new(0.1, 0.09, 0.5, 0.05);

    /// <summary>Every stream unpacked, so a word can't hide behind compression. This is what a determined reader would do.</summary>
    private async Task<string> RawTextOf(string path)
    {
        string unpacked = NewPath;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "qpdf", "qpdf.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "--qdf", "--object-streams=disable", "--stream-data=uncompress", path, unpacked })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        return Encoding.Latin1.GetString(await File.ReadAllBytesAsync(unpacked));
    }

    // ================================================================= redaction

    [Fact]
    public async Task The_test_file_really_has_the_words_in_the_open()
    {
        string path = CreateSecretPdf();

        Assert.Contains(Secret, await RawTextOf(path), StringComparison.Ordinal);
        await using var doc = await Engine.OpenAsync(path);
        Assert.Contains(Secret, await doc.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redacted_words_are_gone_from_the_text_and_from_the_raw_file()
    {
        string path = CreateSecretPdf();
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox), new RedactionArea(1, SecretBox)], output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.DoesNotContain(Secret, await doc.GetPageTextAsync(0), StringComparison.Ordinal);
        Assert.Empty(await doc.FindOnPageAsync(0, Secret));
        Assert.DoesNotContain(Secret, await RawTextOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_outside_the_box_stay_searchable_and_the_box_is_black()
    {
        string path = CreateSecretPdf();
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.Contains(Open, await doc.GetPageTextAsync(0), StringComparison.Ordinal);
        var shown = await doc.RenderPageAsync(0, 600, 800);
        int middle = (88 * shown.Stride) + (200 * 4);
        Assert.True(shown.Pixels[middle] < 40 && shown.Pixels[middle + 1] < 40 && shown.Pixels[middle + 2] < 40);
    }

    [Fact]
    public async Task Pages_with_no_redactions_are_left_alone()
    {
        string path = CreateSecretPdf();
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.Contains($"{Secret} on page two", await doc.GetPageTextAsync(1), StringComparison.Ordinal);
        Assert.Empty(await doc.GetPageImagesAsync(1));
    }

    [Fact]
    public async Task Comments_on_a_redacted_page_are_removed_too()
    {
        string path = CreateSecretPdf(
            pageExtra: "/Annots [8 0 R] ",
            catalogExtra: string.Empty,
            $"<< /Type /Annot /Subtype /Text /Rect [80 690 100 710] /Contents ({Secret} in a comment) /Name /Note >>");
        Assert.Contains($"{Secret} in a comment", await RawTextOf(path), StringComparison.Ordinal);
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox), new RedactionArea(1, SecretBox)], output);

        Assert.DoesNotContain(Secret, await RawTextOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tagged_copy_of_the_words_is_removed_too()
    {
        string path = CreateSecretPdf(
            catalogExtra: "/StructTreeRoot 8 0 R /MarkInfo << /Marked true >> ",
            moreObjects: $"<< /Type /StructTreeRoot /K << /S /P /ActualText ({Secret}) >> >>");
        Assert.Contains("ActualText", await RawTextOf(path), StringComparison.Ordinal);
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox), new RedactionArea(1, SecretBox)], output);

        Assert.DoesNotContain(Secret, await RawTextOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_in_a_reusable_page_fragment_are_removed_too()
    {
        string fragment = $"<< /Type /XObject /Subtype /Form /BBox [0 0 600 800] /Resources << /Font << /F1 5 0 R >> >> /Length {($"BT /F1 24 Tf 72 700 Td ({Secret}) Tj ET").Length} >>\nstream\nBT /F1 24 Tf 72 700 Td ({Secret}) Tj ET\nendstream";
        string path = Handmade(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Fm0 Do"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".Length > 0 ? fragment.Replace("/F1 5 0 R", "/F1 6 0 R", StringComparison.Ordinal) : string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ]);
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], output);

        Assert.DoesNotContain(Secret, await RawTextOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_in_a_form_are_removed_too()
    {
        // A field's answer lives apart from the page.
        string path = Handmade(
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DR << /Font << /F1 5 0 R >> >> /DA (/F1 12 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R] >>",
            Stream("BT /F1 12 Tf 72 300 Td (Label) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V ({Secret}) /Rect [72 690 400 720] /DA (/F1 12 Tf 0 g) /P 3 0 R >>",
        ]);
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], output);

        Assert.DoesNotContain(Secret, await RawTextOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rotated_page_gets_its_box_where_it_was_drawn()
    {
        string path = _files.Create(1, "Words");
        using (var edit = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            edit.Pages[0].Rotate = 90;
            edit.Save(path);
        }

        string output = NewPath;
        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, new NormalizedRect(0.7, 0.05, 0.25, 0.2))], output);

        await using var doc = await Engine.OpenAsync(output);
        var size = doc.PageSizes[0];
        Assert.True(size.Width > size.Height);
        var shown = await doc.RenderPageAsync(0, 400, 300);
        Assert.True(shown.Pixels[(45 * shown.Stride) + (330 * 4)] < 40);
    }

    [Fact]
    public async Task Bookmarks_still_lead_to_a_redacted_page()
    {
        string path = CreateSecretPdf();
        await Engine.SetOutlineAsync(new PdfInputFile(path), [new PdfOutlineItem("Start", 0, [])], path);
        string output = NewPath;

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(0, Assert.Single(await doc.GetOutlineAsync()).PageIndex);
    }

    [Fact]
    public async Task The_original_is_untouched_when_saving_elsewhere()
    {
        string path = CreateSecretPdf();
        byte[] before = await File.ReadAllBytesAsync(path);

        await Engine.RedactAsync(new PdfInputFile(path), [new RedactionArea(0, SecretBox)], NewPath);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    // ================================================================= passwords

    [Fact]
    public async Task A_password_locks_with_aes_256_and_comes_off_again()
    {
        string path = _files.Create(2);
        string locked = NewPath;

        await Engine.ProtectAsync(new PdfInputFile(path), new PdfProtection("open sesame"), locked);

        await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(locked));
        await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(locked, "wrong"));
        await using (var doc = await Engine.OpenAsync(locked, "open sesame"))
        {
            Assert.Equal(2, doc.PageCount);
        }

        // /V 5 /R 6 is the AES-256 scheme.
        string raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(locked));
        Assert.Contains("/V 5", raw.Replace("/V5", "/V 5", StringComparison.Ordinal), StringComparison.Ordinal);

        string open = NewPath;
        await Engine.RemovePasswordAsync(new PdfInputFile(locked, "open sesame"), open);
        await using var unlocked = await Engine.OpenAsync(open);
        Assert.Equal(2, unlocked.PageCount);
    }

    [Fact]
    public async Task Permissions_are_written_into_the_file()
    {
        string path = _files.Create(1);
        string locked = NewPath;

        await Engine.ProtectAsync(new PdfInputFile(path), new PdfProtection("pw", AllowPrinting: false, AllowCopying: false, AllowEditing: false), locked);

        await using var doc = await Engine.OpenAsync(locked, "pw");
        Assert.False(doc.CanPrint);
        Assert.False(doc.CanCopy);
        Assert.False(doc.CanAssemble);
    }

    [Fact]
    public async Task With_every_permission_kept_nothing_is_limited()
    {
        string path = _files.Create(1);
        string locked = NewPath;

        await Engine.ProtectAsync(new PdfInputFile(path), new PdfProtection("pw"), locked);

        await using var doc = await Engine.OpenAsync(locked, "pw");
        Assert.True(doc.CanPrint && doc.CanCopy && doc.CanAssemble);
    }

    [Fact]
    public async Task A_wrong_password_cannot_remove_protection()
    {
        string path = _files.Create(1);
        string locked = NewPath;
        await Engine.ProtectAsync(new PdfInputFile(path), new PdfProtection("right"), locked);
        string output = NewPath;

        await Assert.ThrowsAnyAsync<Exception>(() => Engine.RemovePasswordAsync(new PdfInputFile(locked, "wrong"), output));

        Assert.False(File.Exists(output));
    }

    // ================================================================= repair

    [Fact]
    public async Task A_damaged_file_can_be_repaired()
    {
        string path = _files.Create(3, "Rescue me");

        // Break the file's table of contents.
        byte[] bytes = await File.ReadAllBytesAsync(path);
        string text = Encoding.Latin1.GetString(bytes);
        int xref = text.LastIndexOf("xref", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(path, bytes[..xref]);
        string output = NewPath;

        await Engine.RepairAsync(path, null, output);

        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("Rescue me 3", await doc.GetPageTextAsync(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rubbish_can_not_be_repaired()
    {
        string path = NewPath;
        await File.WriteAllTextAsync(path, "This is not a PDF at all.");
        string output = NewPath;

        await Assert.ThrowsAnyAsync<Exception>(() => Engine.RepairAsync(path, null, output));

        Assert.False(File.Exists(output));
    }

    // ================================================================= metadata and hidden data

    [Fact]
    public async Task Metadata_can_be_changed()
    {
        string path = _files.Create(1);

        await Engine.SetMetadataAsync(new PdfInputFile(path), new PdfDescription("My title", "Me", "A subject", "one, two"), path);

        await using var doc = await Engine.OpenAsync(path);
        var meta = await doc.GetMetadataAsync();
        Assert.Equal("My title", meta.Title);
        Assert.Equal("Me", meta.Author);
    }

    private string CreateWithEverythingHidden() => CreateSecretPdf(
        pageExtra: "/Annots [8 0 R 9 0 R] /AA << /O 10 0 R > > ".Replace("> >", ">>", StringComparison.Ordinal),
        catalogExtra: "/OpenAction 10 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 11 0 R] >> /JavaScript << /Names [(x) 10 0 R] >> >> /Metadata 13 0 R ",
        "<< /Type /Annot /Subtype /Text /Rect [80 690 100 710] /Contents (A private comment) >>",
        "<< /Type /Annot /Subtype /FileAttachment /Rect [80 590 100 610] /FS 11 0 R >>",
        "<< /S /JavaScript /JS (app.alert\\('hello'\\)) >>",
        "<< /Type /Filespec /F (a.txt) /EF << /F 12 0 R >> >>",
        Stream("HIDDENATTACHMENT"),
        "<< /Type /Metadata /Subtype /XML /Length 9 >>\nstream\nHIDDENXML\nendstream");

    [Fact]
    public async Task Hidden_data_is_found()
    {
        var found = await Engine.InspectHiddenDataAsync(new PdfInputFile(CreateWithEverythingHidden()));

        Assert.True(found.HasMetadata);
        Assert.Equal(1, found.Comments);
        Assert.True(found.Attachments >= 1);
        Assert.True(found.Scripts >= 2);
    }

    [Fact]
    public async Task Hidden_data_really_leaves_the_file()
    {
        string path = CreateWithEverythingHidden();
        string output = NewPath;

        await Engine.RemoveHiddenDataAsync(new PdfInputFile(path), new PdfHiddenDataChoice(), output);

        string raw = await RawTextOf(output);
        Assert.DoesNotContain("HIDDENATTACHMENT", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDENXML", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("A private comment", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("app.alert", raw, StringComparison.Ordinal);

        var after = await Engine.InspectHiddenDataAsync(new PdfInputFile(output));
        Assert.Equal(0, after.Comments + after.Attachments + after.Scripts);
        await using var doc = await Engine.OpenAsync(output);
        Assert.Contains(Open, await doc.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_what_was_chosen_is_removed()
    {
        string path = CreateWithEverythingHidden();
        string output = NewPath;

        await Engine.RemoveHiddenDataAsync(new PdfInputFile(path), new PdfHiddenDataChoice(Metadata: false, Comments: true, Attachments: false, HiddenLayers: false, Scripts: false), output);

        var after = await Engine.InspectHiddenDataAsync(new PdfInputFile(output));
        Assert.Equal(0, after.Comments);
        Assert.True(after.Attachments >= 1);
        Assert.True(after.Scripts >= 1);
    }
}
