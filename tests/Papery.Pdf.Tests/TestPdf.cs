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
