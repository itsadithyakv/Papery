using Papery.Pdf;

namespace Papery.TestFiles;

/// <summary>Turns PDF pages into pixels with Papery's own PDFium engine (used to fake scans).</summary>
internal static class Rasterizer
{
    public static async Task<List<GrayImage>> RenderAsync(SharpDoc source, string tempDir, double dpi)
    {
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, $"raster-{Guid.NewGuid():N}.pdf");
        source.Save(tempFile);
        var pages = new List<GrayImage>();
        try
        {
            await using var doc = await PdfEngine.Shared.OpenAsync(tempFile);
            for (int i = 0; i < doc.PageCount; i++)
            {
                var size = doc.PageSizes[i];
                int w = (int)Math.Round(size.Width * dpi / 72);
                int h = (int)Math.Round(size.Height * dpi / 72);
                var bitmap = await doc.RenderPageAsync(i, w, h, PdfRenderOptions.None);
                pages.Add(GrayImage.FromBgra(bitmap));
            }
        }
        finally
        {
            File.Delete(tempFile);
        }

        return pages;
    }
}
