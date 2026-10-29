using static Papery.TestFiles.Layout;

namespace Papery.TestFiles.Generators;

/// <summary>
/// Fake scans: real text is drawn, rendered to pixels at 200 DPI, then skewed,
/// given paper tone, noise, dust and a lid shadow, and saved as image-only PDF pages.
/// There is NO text layer, which is exactly what the OCR stage needs.
/// </summary>
internal static class Scans
{
    private const double Dpi = 200;

    public static async Task ScannedDocumentAsync(string path, string tempDir)
    {
        var source = new SharpDoc();
        string[] titles = ["Quarterly report", "Meeting notes", "Supplier letter", "Left page of a spread", "Right page of a spread", "Upside-down page"];
        for (int i = 0; i < titles.Length; i++)
        {
            var page = AddPage(source, A4);
            using var g = XGraphics.FromPdfPage(page);
            double y = DrawTitle(g, page, titles[i], $"Scanned page {i + 1}");
            foreach (string paragraph in Content.Take(i, 4))
            {
                y = DrawWrapped(g, paragraph, new XFont("Times New Roman", 13), Ink, Margin, y, page.Width.Point - (2 * Margin)) + 10;
            }
        }

        var raw = await Rasterizer.RenderAsync(source, tempDir, Dpi);
        var rng = new Random(42);

        // Each page is skewed by a different amount (degrees), like a real hand-fed scan.
        var scanned = new List<(GrayImage Image, XSize Size)>
        {
            (raw[0].Rotated(0.8), A4),
            (raw[1].Rotated(-1.6), A4),
            (raw[2].Rotated(2.8), A4),
            (GrayImage.SideBySide(raw[3], raw[4]).Rotated(0.5), new XSize(A4.Width * 2, A4.Height)), // double page to split
            (raw[5].Rotated(180.3), A4),
        };

        var output = new SharpDoc();
        output.Info.Title = "Papery scanned document test (image only, no text)";
        foreach (var (image, size) in scanned)
        {
            image.AddScanNoise(rng);
            byte[] jpeg = await ImageEncoder.JpegAsync(image, 0.7f);
            var page = AddPage(output, size);
            using var g = XGraphics.FromPdfPage(page);
            using var x = XImage.FromStream(new MemoryStream(jpeg));
            g.DrawImage(x, 0, 0, size.Width, size.Height);
        }

        output.Save(path);
    }

    /// <summary>A photographed-receipt style JPEG for OCR and "images to PDF" tests.</summary>
    public static async Task ReceiptImageAsync(string path, string tempDir)
    {
        var source = new SharpDoc();
        var page = AddPage(source, new XSize(226, 420)); // 80 mm till roll
        using (var g = XGraphics.FromPdfPage(page))
        {
            // PDFsharp's built-in Windows font lookup only knows the classic core fonts.
            var mono = new XFont("Courier New", 9);
            string[] lines =
            [
                "HARBOUR STREET CAFE", "12 Harbour Street", "", "Lemon cake        3.20", "Flat white        2.90",
                "Orange juice      2.40", "", "TOTAL             8.50", "Card              8.50", "", "Thank you for visiting!",
            ];
            double y = 20;
            foreach (string line in lines)
            {
                g.DrawString(line, mono, Ink, 16, y, XStringFormats.TopLeft);
                y += 14;
            }
        }

        var image = (await Rasterizer.RenderAsync(source, tempDir, 300))[0].Rotated(-3.5);
        image.AddScanNoise(new Random(7));
        await File.WriteAllBytesAsync(path, await ImageEncoder.JpegAsync(image, 0.8f));
    }
}
