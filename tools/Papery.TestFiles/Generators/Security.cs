using System.Text;
using static Papery.TestFiles.Layout;

namespace Papery.TestFiles.Generators;

/// <summary>Password-protected, permission-restricted and deliberately damaged PDFs.</summary>
internal static class Security
{
    public const string UserPassword = "papery";
    public const string OwnerPassword = "papery-owner";

    public static void PasswordFiles(string folder)
    {
        // AES-256 (PDF 2.0 / revision 6): what Papery itself will write.
        var aes256 = SimpleDocument("Protected with AES-256", 3);
        aes256.SecurityHandler.SetEncryptionToV5();
        aes256.SecuritySettings.UserPassword = UserPassword;
        aes256.SecuritySettings.OwnerPassword = OwnerPassword;
        aes256.Save(Path.Combine(folder, "password-aes256.pdf"));

        // AES-128 and RC4-128: older schemes still common in files people receive.
        var aes128 = SimpleDocument("Protected with AES-128", 3);
        aes128.SecurityHandler.SetEncryptionToV4UsingAES(true);
        aes128.SecuritySettings.UserPassword = UserPassword;
        aes128.SecuritySettings.OwnerPassword = OwnerPassword;
        aes128.Save(Path.Combine(folder, "password-aes128.pdf"));

        var rc4 = SimpleDocument("Protected with RC4-128 (legacy)", 3);
        rc4.SecurityHandler.SetEncryptionToV2With128Bits();
        rc4.SecuritySettings.UserPassword = UserPassword;
        rc4.SecuritySettings.OwnerPassword = OwnerPassword;
        rc4.Save(Path.Combine(folder, "password-rc4.pdf"));

        // Opens without a password, but printing, copying and editing are forbidden.
        var restricted = SimpleDocument("Restricted: no printing, copying or editing", 3);
        restricted.SecurityHandler.SetEncryptionToV5();
        restricted.SecuritySettings.OwnerPassword = OwnerPassword;
        restricted.SecuritySettings.PermitPrint = false;
        restricted.SecuritySettings.PermitFullQualityPrint = false;
        restricted.SecuritySettings.PermitExtractContent = false;
        restricted.SecuritySettings.PermitModifyDocument = false;
        restricted.SecuritySettings.PermitAnnotations = false;
        restricted.Save(Path.Combine(folder, "restricted-no-print-no-copy.pdf"));
    }

    /// <param name="jpegBytes">A real JPEG, saved with a .pdf name (a classic real-world mistake).</param>
    public static async Task DamagedFilesAsync(string folder, byte[] jpegBytes)
    {
        // 1. Broken cross-reference table: PDFium should repair this and open all 5 pages.
        byte[] good = ToBytes(SimpleDocument("Damaged cross-reference table", 5));
        byte[] brokenXref = (byte[])good.Clone();
        int startXref = LastIndexOf(brokenXref, "startxref");
        if (startXref < 0)
        {
            throw new InvalidOperationException("Expected a classic xref table in the PDFsharp output.");
        }

        for (int i = startXref + "startxref".Length; i < brokenXref.Length && brokenXref[i] != (byte)'%'; i++)
        {
            if (char.IsAsciiDigit((char)brokenXref[i]))
            {
                brokenXref[i] = (byte)'9'; // offset now points past the end of the file
            }
        }

        int xref = LastIndexOf(brokenXref, "\nxref");
        if (xref >= 0)
        {
            brokenXref[xref + 4] = (byte)'X'; // "xref" -> "xreX"
        }

        await File.WriteAllBytesAsync(Path.Combine(folder, "damaged-broken-xref.pdf"), brokenXref);

        // 2. Truncated download: only the first 60% of a file with an image on every page.
        var withImages = SimpleDocument("Truncated file", 6);
        foreach (var (page, i) in withImages.Pages.Cast<PdfPage>().Select((p, i) => (p, i)))
        {
            byte[] jpeg = await ImageEncoder.JpegAsync(900, 600, Pictures.Landscape(900, 600, i), 0.9f);
            using var g = XGraphics.FromPdfPage(page);
            using var image = XImage.FromStream(new MemoryStream(jpeg));
            g.DrawImage(image, Margin, 400, 480, 320);
        }

        byte[] full = ToBytes(withImages);
        await File.WriteAllBytesAsync(Path.Combine(folder, "damaged-truncated.pdf"), full[..(full.Length * 6 / 10)]);

        // 3. Not a PDF at all, 4. empty file, 5. a PDF header followed by garbage.
        await File.WriteAllBytesAsync(Path.Combine(folder, "damaged-not-a-pdf.pdf"), jpegBytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "damaged-empty.pdf"), []);
        var garbage = new byte[50_000];
        new Random(3).NextBytes(garbage);
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(garbage, 0);
        await File.WriteAllBytesAsync(Path.Combine(folder, "damaged-garbage.pdf"), garbage);
    }

    private static SharpDoc SimpleDocument(string title, int pages)
    {
        var doc = new SharpDoc();
        doc.Info.Title = title;
        for (int n = 1; n <= pages; n++)
        {
            var page = AddPage(doc, A4);
            using var g = XGraphics.FromPdfPage(page);
            double y = DrawTitle(g, page, title, $"Page {n} of {pages}");
            foreach (string paragraph in Content.Take(n, 2))
            {
                y = DrawWrapped(g, paragraph, BodyFont, Ink, Margin, y, page.Width.Point - (2 * Margin)) + 8;
            }
        }

        return doc;
    }

    private static byte[] ToBytes(SharpDoc doc)
    {
        using var stream = new MemoryStream();
        doc.Save(stream, false);
        return stream.ToArray();
    }

    private static int LastIndexOf(byte[] data, string ascii) =>
        data.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(ascii));
}
