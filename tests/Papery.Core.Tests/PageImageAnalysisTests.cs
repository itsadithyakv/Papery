using Papery.Core.Imaging;

namespace Papery.Core.Tests;

public sealed class PageImageAnalysisTests
{
    /// <summary>A white BGRA image to draw into.</summary>
    private sealed class Canvas(int width, int height)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public byte[] Pixels { get; } = Enumerable.Repeat((byte)255, width * height * 4).ToArray();

        public void Dot(int x, int y, byte value = 0)
        {
            if ((uint)x < (uint)Width && (uint)y < (uint)Height)
            {
                int i = ((y * Width) + x) * 4;
                Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = value;
            }
        }

        public void Box(int x, int y, int w, int h)
        {
            for (int j = y; j < y + h; j++)
            {
                for (int i = x; i < x + w; i++)
                {
                    Dot(i, j);
                }
            }
        }

        /// <summary>Lines of "text" 3 px thick, leaning clockwise by <paramref name="degrees"/>.</summary>
        public void TextLines(double degrees)
        {
            double slope = Math.Tan(degrees * Math.PI / 180);
            for (int line = 0; line < 30; line++)
            {
                int baseY = 120 + (line * 26);
                for (int x = 100; x < Width - 100; x++)
                {
                    if (x % 40 > 33)
                    {
                        continue; // gaps between "words"
                    }

                    int y = baseY + (int)Math.Round((x - (Width / 2)) * slope);
                    for (int t = 0; t < 3; t++)
                    {
                        Dot(x, y + t);
                    }
                }
            }
        }
    }

    [Fact]
    public void Bounds_find_the_content_box()
    {
        var c = new Canvas(400, 600);
        c.Box(50, 100, 300, 350);

        var bounds = PageImageAnalysis.ContentBounds(c.Pixels, c.Width, c.Height, c.Width * 4)!.Value;

        Assert.Equal(50 / 400.0, bounds.X, 3);
        Assert.Equal(100 / 600.0, bounds.Y, 3);
        Assert.Equal(300 / 400.0, bounds.Width, 3);
        Assert.Equal(350 / 600.0, bounds.Height, 3);
    }

    [Fact]
    public void Bounds_ignore_a_speck_of_dust()
    {
        var c = new Canvas(400, 600);
        c.Box(50, 100, 300, 350);
        c.Dot(5, 590);

        var bounds = PageImageAnalysis.ContentBounds(c.Pixels, c.Width, c.Height, c.Width * 4)!.Value;

        Assert.Equal(50 / 400.0, bounds.X, 3);
        Assert.Equal(350 / 600.0, bounds.Height, 3);
    }

    [Fact]
    public void Bounds_on_a_scan_ignore_grey_paper_noise_dust_and_the_lid_shadow()
    {
        var c = new Canvas(800, 1100);
        var rng = new Random(7);
        for (int y = 0; y < c.Height; y++)
        {
            for (int x = 0; x < c.Width; x++)
            {
                c.Dot(x, y, (byte)(242 + rng.Next(-9, 10))); // grey, grainy paper
            }
        }

        for (int y = 0; y < c.Height; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                c.Dot(x, y, (byte)((242 * (0.55 + (0.45 * x / 20.0))) + rng.Next(-9, 10))); // scanner-lid shadow
            }
        }

        for (int s = 0; s < 300; s++)
        {
            c.Dot(rng.Next(c.Width), rng.Next(c.Height), (byte)rng.Next(40, 120)); // dust
        }

        c.TextLines(0); // text from x 100 to 700, y 120 to ~900

        var bounds = PageImageAnalysis.ContentBounds(c.Pixels, c.Width, c.Height, c.Width * 4)!.Value;

        Assert.InRange(bounds.X, 95 / 800.0, 105 / 800.0);
        Assert.InRange(bounds.X + bounds.Width, 690 / 800.0, 705 / 800.0);
        Assert.InRange(bounds.Y, 115 / 1100.0, 125 / 1100.0);
    }

    [Fact]
    public void Blank_page_has_no_bounds()
    {
        var c = new Canvas(200, 200);
        Assert.Null(PageImageAnalysis.ContentBounds(c.Pixels, c.Width, c.Height, c.Width * 4));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(-1.6)]
    [InlineData(0.8)]
    [InlineData(-4.5)]
    public void Skew_is_measured_and_straightening_turns_it_back(double lean)
    {
        var c = new Canvas(1000, 1000);
        c.TextLines(lean);

        double straighten = PageImageAnalysis.SkewDegrees(c.Pixels, c.Width, c.Height, c.Width * 4);

        Assert.Equal(-lean, straighten, 1); // within 0.05°
    }

    [Fact]
    public void Straight_text_needs_nothing()
    {
        var c = new Canvas(1000, 1000);
        c.TextLines(0);

        Assert.Equal(0, PageImageAnalysis.SkewDegrees(c.Pixels, c.Width, c.Height, c.Width * 4));
    }

    [Fact]
    public void An_empty_page_is_left_alone()
    {
        var c = new Canvas(500, 500);
        c.Dot(10, 10);

        Assert.Equal(0, PageImageAnalysis.SkewDegrees(c.Pixels, c.Width, c.Height, c.Width * 4));
    }
}
