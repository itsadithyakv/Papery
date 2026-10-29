using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace Papery.Pdf.Tests;

/// <summary>Builds small throw-away PDFs for tests, in a temp folder that is cleaned up afterwards.</summary>
internal sealed class TestPdf : IDisposable
{
    private static readonly Lock FontLock = new();
    private static bool _fontsReady;

    public TestPdf()
    {
        Folder = Directory.CreateTempSubdirectory("papery-tests-").FullName;
        lock (FontLock)
        {
            if (!_fontsReady)
            {
                GlobalFontSettings.UseWindowsFontsUnderWindows = true;
                _fontsReady = true;
            }
        }
    }

    public string Folder { get; }

    /// <summary>Creates an A4 PDF whose page N contains the text "{text} N".</summary>
    public string Create(int pages, string text = "Hello Papery", string? password = null, string name = "test.pdf")
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Arial", 18);
        for (int n = 1; n <= pages; n++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595.28);
            page.Height = XUnit.FromPoint(841.89);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"{text} {n}", font, XBrushes.Black, 72, 72, XStringFormats.TopLeft);
        }

        if (password != null)
        {
            doc.SecurityHandler.SetEncryptionToV5();
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
        }

        string path = Path.Combine(Folder, name);
        doc.Save(path);
        return path;
    }

    /// <summary>
    /// 3 pages with metadata, a 2-level outline, an internal link on page 1 (to page 3)
    /// and a web link annotation, plus a plain-text URL.
    /// </summary>
    public string CreateNavigable(string name = "nav.pdf")
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        doc.Info.Title = "Navigation test";
        doc.Info.Author = "Papery Tests";
        var font = new XFont("Arial", 14);
        var pages = new List<PdfSharp.Pdf.PdfPage>();
        for (int n = 1; n <= 3; n++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(595.28);
            page.Height = XUnit.FromPoint(841.89);
            pages.Add(page);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Page {n} lighthouse", font, XBrushes.Black, 72, 72, XStringFormats.TopLeft);
            if (n == 1)
            {
                g.DrawString("Go to page three", font, XBrushes.Black, 72, 200, XStringFormats.TopLeft);
                page.AddDocumentLink(new PdfSharp.Pdf.PdfRectangle(g.Transformer.WorldToDefaultPage(new XRect(72, 200, 200, 20))), 3);
                g.DrawString("Web", font, XBrushes.Black, 72, 300, XStringFormats.TopLeft);
                page.AddWebLink(new PdfSharp.Pdf.PdfRectangle(g.Transformer.WorldToDefaultPage(new XRect(72, 300, 100, 20))), "https://example.com/annot");
                g.DrawString("See https://example.org/plain for more", font, XBrushes.Black, 72, 400, XStringFormats.TopLeft);
            }
        }

        var chapter = doc.Outlines.Add("Chapter One", pages[0], true);
        chapter.Outlines.Add("Section 1.1", pages[1], false);
        doc.Outlines.Add("Chapter Two", pages[2], false);

        string path = Path.Combine(Folder, name);
        doc.Save(path);
        return path;
    }

    /// <summary>3 pages; page 2 is stuffed with ~150 KB of unique random text, so whether it is gone shows in the file size.</summary>
    public string CreateWithHeavyMiddlePage(string name = "heavy.pdf")
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Courier New", 4);
        var random = new Random(42);
        for (int n = 1; n <= 3; n++)
        {
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"Page {n}", new XFont("Arial", 18), XBrushes.Black, 72, 72, XStringFormats.TopLeft);
            if (n == 2)
            {
                for (int line = 0; line < 3000; line++)
                {
                    string text = Convert.ToHexString(BitConverter.GetBytes(random.NextInt64())) + Convert.ToHexString(BitConverter.GetBytes(random.NextInt64()));
                    g.DrawString(text, font, XBrushes.Black, 20 + (line % 4 * 140), 100 + (line / 4 * 0.9), XStringFormats.TopLeft);
                }
            }
        }

        string path = Path.Combine(Folder, name);
        doc.Save(path);
        return path;
    }

    /// <summary>A PDF anyone can open, whose owner forbids changes (page assembly, editing, copying).</summary>
    public string CreateRestricted(string name = "restricted.pdf")
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        doc.AddPage();
        doc.AddPage();
        doc.SecurityHandler.SetEncryptionToV5();
        doc.SecuritySettings.OwnerPassword = "owner";
        doc.SecuritySettings.PermitAssembleDocument = false;
        doc.SecuritySettings.PermitModifyDocument = false;
        doc.SecuritySettings.PermitExtractContent = false;
        string path = Path.Combine(Folder, name);
        doc.Save(path);
        return path;
    }

    public string CreateRaw(string name, byte[] bytes)
    {
        string path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be closing; the OS temp cleaner will get it.
        }
    }
}
