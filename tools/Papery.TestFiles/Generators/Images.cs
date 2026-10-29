using Windows.Graphics.Imaging;

namespace Papery.TestFiles.Generators;

/// <summary>Image files for "Images to PDF", OCR and HEIC tests, made with Windows' own codecs.</summary>
internal static class Images
{
    /// <summary>Writes the images and returns the bytes of photo.jpg (reused for the "not a PDF" test).</summary>
    public static async Task<byte[]> GenerateAsync(string folder, Action<string> warn)
    {
        Directory.CreateDirectory(folder);
        const int W = 3000, H = 2000;
        byte[] landscape = Pictures.Landscape(W, H, 1);

        byte[] jpeg = await ImageEncoder.JpegAsync(W, H, landscape, 0.9f);
        await File.WriteAllBytesAsync(Path.Combine(folder, "photo.jpg"), jpeg);

        // Stored sideways with EXIF orientation 6: correct apps show it upright (portrait).
        byte[] small = Pictures.Landscape(1600, 1200, 2);
        await File.WriteAllBytesAsync(Path.Combine(folder, "photo-exif-rotated.jpg"),
            await ImageEncoder.JpegAsync(1600, 1200, small, 0.9f, exifOrientation: 6));

        await File.WriteAllBytesAsync(Path.Combine(folder, "diagram-transparent.png"),
            await ImageEncoder.EncodeAsync(BitmapEncoder.PngEncoderId, 1600, 1200, Pictures.Diagram(1600, 1200), BitmapAlphaMode.Straight));

        await File.WriteAllBytesAsync(Path.Combine(folder, "photo.bmp"),
            await ImageEncoder.EncodeAsync(BitmapEncoder.BmpEncoderId, 1200, 800, Pictures.Landscape(1200, 800, 3)));

        await File.WriteAllBytesAsync(Path.Combine(folder, "three-pages.tiff"), await ImageEncoder.TiffAsync(
        [
            (1200, 1600, Pictures.Landscape(1200, 1600, 4)),
            (1200, 1600, Pictures.Landscape(1200, 1600, 5)),
            (1600, 1200, Pictures.Landscape(1600, 1200, 6)),
        ]));

        // HEIC needs the "HEIF Image Extensions" + "HEVC Video Extensions" from the Microsoft Store.
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "photo.heic"),
                await ImageEncoder.EncodeAsync(BitmapEncoder.HeifEncoderId, W, H, landscape));
        }
        catch (Exception ex)
        {
            warn($"photo.heic skipped: this PC has no HEIC encoder ({ex.Message.Trim()}). Copy a photo from a phone instead.");
        }

        // Windows has a WebP decoder but no encoder, so there is no photo.webp here.
        return jpeg;
    }
}
