using Papery.Core.Imaging;

namespace Papery.Core.Tests;

public sealed class DocumentScanTests
{
    private const int Width = 400;
    private const int Height = 300;

    private static byte[] Solid(byte value)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>A white sheet on a dark desk, tipped so its corners are clearly separate.</summary>
    private static byte[] SheetOnDesk(ScanCorners q)
    {
        byte[] pixels = Solid(40);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Inside(q, (double)x / Width, (double)y / Height))
                {
                    int at = ((y * Width) + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = 240;
                }
            }
        }

        return pixels;
    }

    private static bool Inside(ScanCorners q, double x, double y)
    {
        ScanPoint[] p = [q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft];
        for (int i = 0; i < 4; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % 4];
            if (((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void Near(ScanPoint expected, ScanPoint actual)
    {
        Assert.InRange(actual.X, expected.X - 0.03, expected.X + 0.03);
        Assert.InRange(actual.Y, expected.Y - 0.03, expected.Y + 0.03);
    }

    private static readonly ScanCorners Tipped = new(new(0.25, 0.15), new(0.8, 0.2), new(0.72, 0.85), new(0.18, 0.8));

    [Fact]
    public void Finds_the_corners_of_a_tipped_sheet()
    {
        var found = DocumentScan.FindCorners(SheetOnDesk(Tipped), Width, Height, Width * 4);

        Assert.NotNull(found);
        Near(Tipped.TopLeft, found.TopLeft);
        Near(Tipped.TopRight, found.TopRight);
        Near(Tipped.BottomRight, found.BottomRight);
        Near(Tipped.BottomLeft, found.BottomLeft);
    }

    [Theory]
    [InlineData(255)] // nothing but paper
    [InlineData(0)] // nothing at all
    public void No_sheet_gives_no_corners(byte shade)
    {
        Assert.Null(DocumentScan.FindCorners(Solid(shade), Width, Height, Width * 4));
    }

    [Fact]
    public void Straightening_leaves_only_the_sheet()
    {
        var (pixels, w, h) = DocumentScan.Straighten(SheetOnDesk(Tipped), Width, Height, Width * 4, Tipped);

        Assert.InRange(w, 200, 240); // about as wide as the sheet is in pixels
        int dark = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128)
            {
                dark++;
            }
        }

        Assert.True(dark < w * h * 0.02, $"{dark} of {w * h} pixels are still desk");
    }

    [Fact]
    public void Straightening_a_whole_picture_changes_nothing()
    {
        byte[] picture = Solid(90);
        picture[((10 * Width) + 20) * 4] = 200;

        var (pixels, w, h) = DocumentScan.Straighten(picture, Width, Height, Width * 4, ScanCorners.WholePicture);

        Assert.Equal((Width, Height), (w, h));
        Assert.Equal(picture, pixels);
    }

    [Fact]
    public void Black_and_white_has_no_greys_and_keeps_ink_under_a_shadow()
    {
        // Paper that gets darker from left to right, with ink on both sides.
        byte[] page = Solid(0);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = ((y * Width) + x) * 4;
                byte paper = (byte)(230 - (x * 120 / Width));
                bool ink = y is > 100 and < 110 && (x is > 40 and < 100 || x is > 300 and < 360);
                pixels(page, at, ink ? (byte)(paper * 0.4) : paper);
            }
        }

        byte[] result = DocumentScan.Enhance(page, Width, Height, Width * 4, ScanLook.BlackAndWhite);

        Assert.All(result.Where((_, i) => i % 4 == 0), v => Assert.True(v is 0 or 255));
        Assert.Equal(0, result[((105 * Width) + 70) * 4]);   // ink on bright paper
        Assert.Equal(0, result[((105 * Width) + 330) * 4]);  // ink in the shadow
        Assert.Equal(255, result[((20 * Width) + 330) * 4]); // the shadowed paper itself stays white

        static void pixels(byte[] p, int at, byte v) => p[at] = p[at + 1] = p[at + 2] = v;
    }

    [Theory]
    [InlineData(ScanLook.Grey)]
    [InlineData(ScanLook.Colour)]
    public void Enhancing_whitens_dull_paper(ScanLook look)
    {
        byte[] dull = Solid(170);
        for (int x = 0; x < Width; x++)
        {
            int at = ((150 * Width) + x) * 4;
            dull[at] = dull[at + 1] = dull[at + 2] = 60; // a line of ink
        }

        // Enough ink to count, as a real page has.
        for (int y = 140; y < 160; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = ((y * Width) + x) * 4;
                dull[at] = dull[at + 1] = dull[at + 2] = 60;
            }
        }

        byte[] result = DocumentScan.Enhance(dull, Width, Height, Width * 4, look);

        Assert.Equal(255, result[((10 * Width) + 10) * 4]);
        Assert.Equal(0, result[((150 * Width) + 10) * 4]);
    }
}
