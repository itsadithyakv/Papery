using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Papery.Core.Storage;
using Papery.Pdf;

namespace Papery.Services;

/// <summary>Turns PDFium's BGRA pixels into something XAML can show, applying the reading mode.</summary>
internal static class PageBitmaps
{
    // Dark mode maps white paper to #1E1E1E and black ink to #E0E0E0 (soft, not harsh pure black/white).
    private const int DarkPaper = 0x1E;
    private const int DarkInk = 0xE0;

    // Sepia: paper and ink colours as (B, G, R).
    private static readonly (int B, int G, int R) SepiaPaper = (217, 238, 250);
    private static readonly (int B, int G, int R) SepiaInk = (28, 40, 60);

    /// <summary>The colour of blank paper in each reading mode (behind pages while they render).</summary>
    public static Windows.UI.Color PaperColor(ReadingMode mode) => mode switch
    {
        ReadingMode.Dark => Windows.UI.Color.FromArgb(255, 0x1E, 0x1E, 0x1E),
        ReadingMode.Sepia => Windows.UI.Color.FromArgb(255, 250, 238, 217),
        _ => Microsoft.UI.Colors.White,
    };

    /// <summary>
    /// Turns a rendered page by quarter turns clockwise, so tools that work on the page as the user
    /// sees it (crop, straighten) can use plain screen coordinates. Any thread.
    /// </summary>
    public static PdfBitmap Turn(PdfBitmap bitmap, int quarterTurns)
    {
        int q = ((quarterTurns % 4) + 4) % 4;
        if (q == 0)
        {
            return bitmap;
        }

        int w = bitmap.Width, h = bitmap.Height;
        int outW = q % 2 == 0 ? w : h, outH = q % 2 == 0 ? h : w;
        var pixels = new byte[outW * outH * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (int nx, int ny) = q switch
                {
                    1 => (h - 1 - y, x),
                    2 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x),
                };
                Buffer.BlockCopy(bitmap.Pixels, (y * bitmap.Stride) + (x * 4), pixels, ((ny * outW) + nx) * 4, 4);
            }
        }

        return new PdfBitmap(outW, outH, outW * 4, pixels);
    }

    /// <summary>Must be called on the UI thread.</summary>
    public static WriteableBitmap ToWriteableBitmap(PdfBitmap bitmap)
    {
        var result = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var stream = result.PixelBuffer.AsStream())
        {
            if (bitmap.Stride == bitmap.Width * 4)
            {
                stream.Write(bitmap.Pixels, 0, bitmap.Pixels.Length);
            }
            else
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    stream.Write(bitmap.Pixels, y * bitmap.Stride, bitmap.Width * 4);
                }
            }
        }

        result.Invalidate();
        return result;
    }

    /// <summary>
    /// Recolours a rendered page (or tile) in place for Dark or Sepia reading.
    /// Pictures are left untouched so photos still look right: <paramref name="imageAreas"/>
    /// are where they are on the page. A picture covering most of the page is a scan of
    /// text, so it IS recoloured. Safe to call on any thread.
    /// </summary>
    /// <param name="pageWidth">Full page width in pixels at this render scale.</param>
    /// <param name="offsetX">Where this bitmap starts within the full page (non-zero for tiles).</param>
    public static void ApplyReadingMode(
        PdfBitmap bitmap, ReadingMode mode, IReadOnlyList<NormalizedRect> imageAreas,
        int pageWidth, int pageHeight, int offsetX = 0, int offsetY = 0)
    {
        if (mode == ReadingMode.Light)
        {
            return;
        }

        // Pixel rectangles (in this bitmap's coordinates) to leave alone.
        var keep = imageAreas
            .Where(a => a.Width * a.Height < 0.75)
            .Select(a => (
                X1: (int)(a.X * pageWidth) - offsetX,
                Y1: (int)(a.Y * pageHeight) - offsetY,
                X2: (int)Math.Ceiling(a.Right * pageWidth) - offsetX,
                Y2: (int)Math.Ceiling(a.Bottom * pageHeight) - offsetY))
            .Where(r => r.X2 > 0 && r.Y2 > 0 && r.X1 < bitmap.Width && r.Y1 < bitmap.Height)
            .ToArray();

        byte[] px = bitmap.Pixels;
        var spans = new List<(int From, int To)>();
        for (int y = 0; y < bitmap.Height; y++)
        {
            // Work out which x ranges on this row are outside every picture.
            spans.Clear();
            spans.Add((0, bitmap.Width));
            foreach (var r in keep)
            {
                if (y < r.Y1 || y >= r.Y2)
                {
                    continue;
                }

                for (int s = spans.Count - 1; s >= 0; s--)
                {
                    var (from, to) = spans[s];
                    int cutFrom = Math.Max(from, r.X1), cutTo = Math.Min(to, r.X2);
                    if (cutFrom >= cutTo)
                    {
                        continue;
                    }

                    spans.RemoveAt(s);
                    if (cutTo < to)
                    {
                        spans.Insert(s, (cutTo, to));
                    }

                    if (from < cutFrom)
                    {
                        spans.Insert(s, (from, cutFrom));
                    }
                }
            }

            int row = y * bitmap.Stride;
            foreach (var (from, to) in spans)
            {
                for (int x = from; x < to; x++)
                {
                    int i = row + (x * 4);
                    if (mode == ReadingMode.Dark)
                    {
                        Darken(px, i);
                    }
                    else
                    {
                        Sepia(px, i);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Flips brightness but keeps colour: white→dark grey, black→light grey, and coloured
    /// text stays roughly the same hue (a plain colour invert would turn red text cyan).
    /// </summary>
    private static void Darken(byte[] px, int i)
    {
        int b = px[i], g = px[i + 1], r = px[i + 2];
        int luma = ((r * 299) + (g * 587) + (b * 114)) / 1000;
        int shift = 255 - (2 * luma);
        px[i] = Squeeze(b + shift);
        px[i + 1] = Squeeze(g + shift);
        px[i + 2] = Squeeze(r + shift);

        static byte Squeeze(int v) => (byte)(DarkPaper + ((Math.Clamp(v, 0, 255) * (DarkInk - DarkPaper)) / 255));
    }

    private static void Sepia(byte[] px, int i)
    {
        int luma = ((px[i + 2] * 299) + (px[i + 1] * 587) + (px[i] * 114)) / 1000;
        px[i] = (byte)(SepiaInk.B + ((SepiaPaper.B - SepiaInk.B) * luma / 255));
        px[i + 1] = (byte)(SepiaInk.G + ((SepiaPaper.G - SepiaInk.G) * luma / 255));
        px[i + 2] = (byte)(SepiaInk.R + ((SepiaPaper.R - SepiaInk.R) * luma / 255));
    }
}
