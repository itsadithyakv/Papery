using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Papery.Pdf.Editing;
using PdfSharp.Drawing;

namespace Papery.Pdf.Tests;

public sealed partial class PdfLaterToolsTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath(string extension = ".pdf") => Path.Combine(_files.Folder, $"t-{Guid.NewGuid():N}{extension}");

    private string CreateLetter()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        using (var g = XGraphics.FromPdfPage(page))
        {
            g.DrawString("The Big Heading", new XFont("Arial", 28, XFontStyleEx.Bold), XBrushes.Black, 72, 90);
            var body = new XFont("Arial", 12);
            g.DrawString("First line of a paragraph & more", body, XBrushes.Black, 72, 150);
            g.DrawString("second line of the same one", body, XBrushes.Black, 72, 166);
            g.DrawString("A new paragraph after a gap", body, XBrushes.Black, 72, 240);
            g.DrawString("Left cell", body, XBrushes.Black, 72, 320);
            g.DrawString("Right cell", body, XBrushes.Black, 400, 320);
        }

        var second = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(second))
        {
            g.DrawString("Second page words", new XFont("Arial", 12), XBrushes.Black, 72, 150);
        }

        string path = NewPath();
        doc.Save(path);
        return path;
    }

    // ================================================================= words

    [Fact]
    public async Task Words_come_with_their_places_and_looks()
    {
        await using var doc = await Engine.OpenAsync(CreateLetter());

        var words = await doc.GetPageWordsAsync(0);

        var big = words.First(w => w.Text == "Heading");
        Assert.True(big.Bold);
        Assert.InRange(big.FontSize, 26, 30);
        var small = words.First(w => w.Text == "paragraph");
        Assert.False(small.Bold);
        Assert.InRange(small.FontSize, 11, 13);
        Assert.True(small.Area.Y > big.Area.Y);
    }

    [Fact]
    public async Task The_whole_line_can_be_found_from_one_character()
    {
        await using var doc = await Engine.OpenAsync(CreateLetter());
        var words = await doc.GetPageWordsAsync(0);
        var word = words.First(w => w.Text == "Heading");

        var line = await doc.GetLineAtAsync(0, word.Start);

        Assert.Equal(["The", "Big", "Heading"], line.Select(w => w.Text));
    }

    // ================================================================= Word

    private static XmlDocument ReadDocument(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var document = new XmlDocument();
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        document.Load(stream);
        return document;
    }

    [Fact]
    public async Task A_word_file_has_the_text_in_order_and_is_valid_xml()
    {
        string output = NewPath(".docx");
        await using var doc = await Engine.OpenAsync(CreateLetter());

        await Engine.ExportWordAsync(doc, output);

        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        var xml = ReadDocument(output);
        string text = xml.DocumentElement!.InnerText;
        Assert.Contains("The Big Heading", text, StringComparison.Ordinal);
        Assert.Contains("First line of a paragraph & more second line of the same one", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Heading", StringComparison.Ordinal) < text.IndexOf("Second page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paragraphs_breaks_headings_and_columns_are_kept()
    {
        await using var doc = await Engine.OpenAsync(CreateLetter());
        var words = await doc.GetPageWordsAsync(0);

        var paragraphs = DocxLayout.Paragraphs(words);

        Assert.Equal(4, paragraphs.Count);
        Assert.True(paragraphs[0][0].Bold);
        Assert.True(paragraphs[0][0].FontSize > paragraphs[1][0].FontSize);
        Assert.Contains('\t', paragraphs[3][0].Text);
    }

    [Fact]
    public async Task Pictures_go_into_a_word_file()
    {
        byte[] pixels = new byte[100 * 100 * 4];
        Array.Fill(pixels, (byte)200);
        string path = NewPath();
        await Engine.ImagesToPdfAsync([new PdfImagePage(100, 100, () => Task.FromResult(new PdfImageData(null, new PdfBitmap(100, 100, 400, pixels))))], new ImagePageOptions(), path);
        string output = NewPath(".docx");
        await using var doc = await Engine.OpenAsync(path);

        await Engine.ExportWordAsync(doc, output);

        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("word/media/image1.jpg"));
        Assert.Contains("rIdImg1", new StreamReader(zip.GetEntry("word/_rels/document.xml.rels")!.Open()).ReadToEnd(), StringComparison.Ordinal);
        Assert.Contains("rIdImg1", ReadDocument(output).OuterXml, StringComparison.Ordinal);
    }

    // ================================================================= digital signatures

    private static X509Certificate2 TestCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Papery Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    [GeneratedRegex(@"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]")]
    private static partial Regex ByteRange();

    /// <summary>Checks a signature the way a viewer does: over the bytes it claims to cover.</summary>
    private static void CheckSignature(byte[] file)
    {
        var match = ByteRange().Match(Encoding.Latin1.GetString(file));
        Assert.True(match.Success, "no signature in the file");
        int a = int.Parse(match.Groups[1].Value), b = int.Parse(match.Groups[2].Value), c = int.Parse(match.Groups[3].Value), d = int.Parse(match.Groups[4].Value);
        Assert.Equal(file.Length, c + d); // it covers the file to the very end
        byte[] covered = [.. file.AsSpan(a, b), .. file.AsSpan(c, d)];
        string hex = Encoding.ASCII.GetString(file, a + b + 1, c - (a + b) - 2);
        var signed = new SignedCms(new ContentInfo(covered), detached: true);
        signed.Decode(Convert.FromHexString(hex.TrimEnd('0').Length % 2 == 0 ? hex.TrimEnd('0') : hex.TrimEnd('0') + "0"));
        signed.CheckSignature(verifySignatureOnly: true);
    }

    [Fact]
    public async Task A_signature_verifies_and_the_file_still_opens()
    {
        using var certificate = TestCertificate();
        string path = _files.Create(2, "Signed words");
        string output = NewPath();

        await Engine.SignAsync(new PdfInputFile(path), new PdfSignatureRequest(certificate, "I agree", "Kochi"), output);

        CheckSignature(await File.ReadAllBytesAsync(output));
        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("Signed words 2", await doc.GetPageTextAsync(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_signed_file_breaks_the_signature()
    {
        using var certificate = TestCertificate();
        string output = NewPath();
        await Engine.SignAsync(new PdfInputFile(_files.Create(1)), new PdfSignatureRequest(certificate), output);
        byte[] file = await File.ReadAllBytesAsync(output);
        file[50] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => CheckSignature(file));
    }

    [Fact]
    public void The_certificate_list_can_be_read()
    {
        Assert.NotNull(Engine.SigningCertificates());
    }
}
