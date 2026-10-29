using Papery.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Papery.TestFiles;

/// <summary>An 8-bit greyscale image, used to fake scanned pages.</summary>
internal sealed class GrayImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    public static GrayImage FromBgra(PdfBitmap bitmap)
    {
        var gray = new byte[bitmap.Width * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            int row = y * bitmap.Stride;
            for (int x = 0; x < bitmap.Width; x++)
            {
                int i = row + (x * 4);
                gray[(y * bitmap.Width) + x] = (byte)(
                    (bitmap.Pixels[i] * 0.114) + (bitmap.Pixels[i + 1] * 0.587) + (bitmap.Pixels[i + 2] * 0.299));
            }
        }

        return new GrayImage(bitmap.Width, bitmap.Height, gray);
    }

    public static GrayImage SideBySide(GrayImage left, GrayImage right)
    {
        int height = Math.Max(left.Height, right.Height);
        var result = new GrayImage(left.Width + right.Width, height, new byte[(left.Width + right.Width) * height]);
        Array.Fill(result.Pixels, (byte)255);
        for (int y = 0; y < height; y++)
        {
            if (y < left.Height)
            {
                Array.Copy(left.Pixels, y * left.Width, result.Pixels, y * result.Width, left.Width);
            }

            if (y < right.Height)
            {
                Array.Copy(right.Pixels, y * right.Width, result.Pixels, (y * result.Width) + left.Width, right.Width);
            }
        }

        return result;
    }

    /// <summary>Rotates around the centre (bilinear), filling uncovered corners with <paramref name="background"/>.</summary>
    public GrayImage Rotated(double degrees, byte background = 255)
    {
        double rad = degrees * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = Width / 2.0, cy = Height / 2.0;
        var dst = new byte[Pixels.Length];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // Inverse mapping: find where this output pixel comes from in the source.
                double dx = x - cx, dy = y - cy;
                double sx = (cos * dx) + (sin * dy) + cx;
                double sy = (-sin * dx) + (cos * dy) + cy;
                dst[(y * Width) + x] = Sample(sx, sy, background);
            }
        }

        return new GrayImage(Width, Height, dst);
    }

    /// <summary>Adds paper tone, sensor noise, dust specks and a scanner-lid shadow on the left edge.</summary>
    public void AddScanNoise(Random rng)
    {
        for (int i = 0; i < Pixels.Length; i++)
        {
            double paper = 242 + rng.Next(-3, 4);
            int v = (int)(Pixels[i] * (paper / 255.0)) + rng.Next(-9, 10);
            Pixels[i] = (byte)Math.Clamp(v, 0, 255);
        }

        int shadow = Width / 40;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < shadow; x++)
            {
                int i = (y * Width) + x;
                Pixels[i] = (byte)(Pixels[i] * (0.55 + (0.45 * x / shadow)));
            }
        }

        for (int s = 0; s < 400; s++)
        {
            int px = rng.Next(Width - 3), py = rng.Next(Height - 3), size = rng.Next(1, 4);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Pixels[((py + y) * Width) + px + x] = (byte)rng.Next(40, 120);
                }
            }
        }
    }

    private byte Sample(double x, double y, byte background)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= Width - 1 || y0 >= Height - 1)
        {
            return background;
        }

        double fx = x - x0, fy = y - y0;
        int i = (y0 * Width) + x0;
        double top = (Pixels[i] * (1 - fx)) + (Pixels[i + 1] * fx);
        double bottom = (Pixels[i + Width] * (1 - fx)) + (Pixels[i + Width + 1] * fx);
        return (byte)((top * (1 - fy)) + (bottom * fy));
    }
}

/// <summary>Encodes pixels to image files using the codecs built into Windows (WIC).</summary>
internal static class ImageEncoder
{
    public static Task<byte[]> JpegAsync(GrayImage image, float quality)
    {
        // The WinRT encoder rejects Gray8 input, so expand grey to BGRA (it still looks grey).
        var bgra = new byte[image.Pixels.Length * 4];
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            byte v = image.Pixels[i];
            (bgra[i * 4], bgra[(i * 4) + 1], bgra[(i * 4) + 2], bgra[(i * 4) + 3]) = (v, v, v, 255);
        }

        return JpegAsync(image.Width, image.Height, bgra, quality);
    }

    public static Task<byte[]> JpegAsync(int width, int height, byte[] bgra, float quality, ushort? exifOrientation = null) =>
        EncodeAsync(BitmapEncoder.JpegEncoderId, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, bgra, quality, exifOrientation);

    public static Task<byte[]> EncodeAsync(Guid encoderId, int width, int height, byte[] bgra, BitmapAlphaMode alpha = BitmapAlphaMode.Ignore) =>
        EncodeAsync(encoderId, BitmapPixelFormat.Bgra8, alpha, width, height, bgra);

    /// <summary>A multi-page TIFF, one frame per image.</summary>
    public static async Task<byte[]> TiffAsync(IReadOnlyList<(int Width, int Height, byte[] Bgra)> frames)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, stream);
        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                await encoder.GoToNextFrameAsync();
            }

            var (w, h, px) = frames[i];
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, px);
        }

        await encoder.FlushAsync();
        return await ReadAllAsync(stream);
    }

    private static async Task<byte[]> EncodeAsync(
        Guid encoderId, BitmapPixelFormat format, BitmapAlphaMode alpha, int width, int height, byte[] pixels,
        float? quality = null, ushort? exifOrientation = null)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (quality is float q)
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(q, PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream, options);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        }

        encoder.SetPixelData(format, alpha, (uint)width, (uint)height, 96, 96, pixels);

        if (exifOrientation is ushort orientation)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, PropertyType.UInt16),
            });
        }

        await encoder.FlushAsync();
        return await ReadAllAsync(stream);
    }

    private static async Task<byte[]> ReadAllAsync(InMemoryRandomAccessStream stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

/// <summary>Draws synthetic "photos" so image tests do not need real photographs.</summary>
internal static class Pictures
{
    /// <summary>A simple landscape: sky gradient, sun, two hills. BGRA, opaque.</summary>
    public static byte[] Landscape(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[width * height * 4];
        double sunX = width * (0.2 + (rng.NextDouble() * 0.6)), sunY = height * 0.28, sunR = height * 0.09;
        double phase = rng.NextDouble() * 6;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = (double)y / height;
                double r = 120 + (100 * t), g = 170 + (60 * t), b = 235 - (20 * t); // sky
                if (Math.Pow(x - sunX, 2) + Math.Pow(y - sunY, 2) < sunR * sunR)
                {
                    (r, g, b) = (255, 214, 120);
                }

                double hill1 = height * (0.62 + (0.06 * Math.Sin((x / (double)width * 5) + phase)));
                double hill2 = height * (0.75 + (0.05 * Math.Sin((x / (double)width * 9) + (phase * 2))));
                if (y > hill1)
                {
                    (r, g, b) = (88, 140, 80);
                }

                if (y > hill2)
                {
                    (r, g, b) = (60, 105, 58);
                }

                int i = ((y * width) + x) * 4;
                int noise = rng.Next(-6, 7);
                px[i] = (byte)Math.Clamp(b + noise, 0, 255);
                px[i + 1] = (byte)Math.Clamp(g + noise, 0, 255);
                px[i + 2] = (byte)Math.Clamp(r + noise, 0, 255);
                px[i + 3] = 255;
            }
        }

        return px;
    }

    /// <summary>Coloured random noise: compresses badly, which is what makes big test files big.</summary>
    public static byte[] Noise(int width, int height, int seed)
    {
        var px = new byte[width * height * 4];
        new Random(seed).NextBytes(px);
        for (int i = 3; i < px.Length; i += 4)
        {
            px[i] = 255;
        }

        return px;
    }

    /// <summary>Coloured shapes on a transparent background (for PNG transparency tests). BGRA, straight alpha.</summary>
    public static byte[] Diagram(int width, int height)
    {
        var px = new byte[width * height * 4];
        void Fill(Func<int, int, bool> inside, byte b, byte g, byte r, byte a)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (inside(x, y))
                    {
                        int i = ((y * width) + x) * 4;
                        (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, a);
                    }
                }
            }
        }

        Fill((x, y) => x > width * 0.1 && x < width * 0.45 && y > height * 0.15 && y < height * 0.6, 61, 83, 229, 255);
        Fill((x, y) => Math.Pow(x - (width * 0.65), 2) + Math.Pow(y - (height * 0.55), 2) < Math.Pow(height * 0.28, 2), 200, 140, 40, 200);
        Fill((x, y) => y > height * 0.8 && y < height * 0.86 && x > width * 0.1 && x < width * 0.9, 40, 40, 40, 255);
        return px;
    }
}
