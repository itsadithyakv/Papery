namespace Papery.Pdf.Editing;

/// <summary>A ready-made way to improve a picture.</summary>
public enum PictureLook
{
    /// <summary>Only the sliders apply.</summary>
    Original,

    /// <summary>Corrects a washed-out picture or a colour cast.</summary>
    Auto,

    /// <summary>For scans: even, white paper with no shadows, and darker ink. Colour stamps stay coloured.</summary>
    Document,

    Grey,

    /// <summary>Pure black ink on pure white paper, stored without any loss.</summary>
    BlackAndWhite,

    /// <summary>Like Auto, with richer colour. For photos.</summary>
    Vivid,
}

/// <summary>How to change the pictures in a PDF. Sliders run from -100 to 100, or 0 to 100 for sharpen and smooth.</summary>
public sealed record PictureFilterOptions(PictureLook Look, int Brightness = 0, int Contrast = 0, int Sharpen = 0, int Smooth = 0, bool Straighten = false)
{
    public bool DoesNothing => Look == PictureLook.Original && Brightness == 0 && Contrast == 0 && Sharpen == 0 && Smooth == 0 && !Straighten;
}

/// <summary>The pixel work behind filters. Knows nothing about PDFs, so each step can be tested on its own.</summary>
internal static class PictureFilter
{
    // Blocks across the longer side when working out the paper's colour. Fewer can't follow a shadow; more mistake big letters for shadow.
    private const int PaperBlocks = 64;

    // However dark an area, paper is never taken as darker than this share of the usual paper, so photos on a page stay photos.
    private const double DarkestPaper = 0.55;

    // Scan tilt beyond this is a design choice, not a slip of the paper.
    private const double LargestSkew = 5;
    private const double SmallestSkew = 0.1;

    public static PdfBitmap Apply(PdfBitmap source, PictureFilterOptions options)
    {
        int width = source.Width, height = source.Height;
        byte[] pixels = Tight(source);

        if (options.Straighten)
        {
            double skew = FindSkew(pixels, width, height);
            if (Math.Abs(skew) >= SmallestSkew)
            {
                pixels = Rotate(pixels, width, height, skew);
            }
        }

        if (options.Smooth > 0)
        {
            pixels = Smooth(pixels, width, height, options.Smooth);
        }

        switch (options.Look)
        {
            case PictureLook.Auto:
                StretchLevels(pixels);
                break;
            case PictureLook.Vivid:
                StretchLevels(pixels);
                Saturate(pixels, 1.3);
                break;
            case PictureLook.Grey:
                MakeGrey(pixels);
                StretchLevels(pixels);
                break;
            case PictureLook.Document:
            case PictureLook.BlackAndWhite:
                EvenOutPaper(pixels, width, height);
                break;
        }

        Tone(pixels, options.Brightness, options.Contrast);

        if (options.Look == PictureLook.BlackAndWhite)
        {
            BlackAndWhite(pixels, width, height);
        }
        else if (options.Sharpen > 0)
        {
            pixels = Sharpen(pixels, width, height, options.Sharpen);
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    private static byte[] Tight(PdfBitmap source)
    {
        byte[] pixels = new byte[source.Width * source.Height * 4];
        for (int y = 0; y < source.Height; y++)
        {
            Buffer.BlockCopy(source.Pixels, y * source.Stride, pixels, y * source.Width * 4, source.Width * 4);
        }

        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return pixels;
    }

    private static int Luma(byte[] p, int at) => ((p[at + 2] * 299) + (p[at + 1] * 587) + (p[at] * 114)) / 1000;

    private static void ApplyTable(byte[] pixels, byte[] table)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = table[pixels[i]];
            pixels[i + 1] = table[pixels[i + 1]];
            pixels[i + 2] = table[pixels[i + 2]];
        }
    }

    // ================================================================= colour and brightness

    /// <summary>Stretches each colour so its darkest and brightest values reach black and white, which also removes a colour cast.</summary>
    private static void StretchLevels(byte[] pixels)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            var histogram = new int[256];
            for (int i = channel; i < pixels.Length; i += 4)
            {
                histogram[pixels[i]]++;
            }

            var (low, high) = Ends(histogram, pixels.Length / 4, 0.005);
            if (high - low < 32)
            {
                continue; // nearly one flat colour: stretching it would only show up the noise
            }

            double scale = 255.0 / (high - low);
            for (int i = channel; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)Math.Clamp((pixels[i] - low) * scale, 0, 255);
            }
        }
    }

    private static (int Low, int High) Ends(int[] histogram, int total, double share)
    {
        int ignore = (int)(total * share), seen = 0, low = 0, high = 255;
        for (int i = 0; i < 256; i++)
        {
            seen += histogram[i];
            if (seen > ignore)
            {
                low = i;
                break;
            }
        }

        seen = 0;
        for (int i = 255; i >= 0; i--)
        {
            seen += histogram[i];
            if (seen > ignore)
            {
                high = i;
                break;
            }
        }

        return (low, high);
    }

    private static void MakeGrey(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)Luma(pixels, i);
        }
    }

    private static void Saturate(byte[] pixels, double amount)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int grey = Luma(pixels, i);
            pixels[i] = (byte)Math.Clamp(grey + ((pixels[i] - grey) * amount), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(grey + ((pixels[i + 1] - grey) * amount), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(grey + ((pixels[i + 2] - grey) * amount), 0, 255);
        }
    }

    private static void Tone(byte[] pixels, int brightness, int contrast)
    {
        if (brightness == 0 && contrast == 0)
        {
            return;
        }

        double lift = Math.Clamp(brightness, -100, 100) * 1.275;
        double factor = contrast >= 0 ? 1 + (Math.Min(contrast, 100) / 50.0) : 1 + (Math.Max(contrast, -100) / 100.0);
        byte[] table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            table[v] = (byte)Math.Clamp(128 + ((v - 128) * factor) + lift, 0, 255);
        }

        ApplyTable(pixels, table);
    }

    // ================================================================= cleaning up a scan

    /// <summary>
    /// Works out the paper's colour area by area, then divides by it. That takes out shadows, a fold's
    /// darkening and yellowed paper together, because all of them are just "the paper here is darker".
    /// </summary>
    private static void EvenOutPaper(byte[] pixels, int width, int height)
    {
        int block = Math.Max(8, Math.Max(width, height) / PaperBlocks);
        int across = ((width - 1) / block) + 1, down = ((height - 1) / block) + 1;
        var paper = new double[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            paper[channel] = PaperGrid(pixels, width, height, block, across, down, channel);
        }

        Parallel.For(0, height, y =>
        {
            double gy = Math.Clamp(((y + 0.5) / block) - 0.5, 0, down - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, down - 1);
            double fy = gy - y0;
            for (int x = 0; x < width; x++)
            {
                double gx = Math.Clamp(((x + 0.5) / block) - 0.5, 0, across - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, across - 1);
                double fx = gx - x0;
                int at = ((y * width) + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double[] grid = paper[channel];
                    double top = (grid[(y0 * across) + x0] * (1 - fx)) + (grid[(y0 * across) + x1] * fx);
                    double bottom = (grid[(y1 * across) + x0] * (1 - fx)) + (grid[(y1 * across) + x1] * fx);
                    double here = (top * (1 - fy)) + (bottom * fy);
                    pixels[at + channel] = (byte)Math.Min(255, pixels[at + channel] * 255 / Math.Max(1, here));
                }
            }
        });

        // Paper that's nearly white becomes white, and ink darkens a little.
        var histogram = new int[256];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            histogram[Luma(pixels, i)]++;
        }

        var (low, _) = Ends(histogram, pixels.Length / 4, 0.01);
        low = Math.Min(low, 96);
        const int White = 238;
        byte[] table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            table[v] = (byte)Math.Clamp(255 * Math.Pow(Math.Clamp((double)(v - low) / (White - low), 0, 1), 1.15), 0, 255);
        }

        ApplyTable(pixels, table);
    }

    private static double[] PaperGrid(byte[] pixels, int width, int height, int block, int across, int down, int channel)
    {
        double[] grid = new double[across * down];
        Parallel.For(0, down, row =>
        {
            var histogram = new int[256];
            for (int column = 0; column < across; column++)
            {
                Array.Clear(histogram);
                int count = 0;
                int bottom = Math.Min(height, (row + 1) * block), right = Math.Min(width, (column + 1) * block);
                for (int y = row * block; y < bottom; y += 2)
                {
                    for (int x = column * block; x < right; x += 2)
                    {
                        histogram[pixels[(((y * width) + x) * 4) + channel]]++;
                        count++;
                    }
                }

                // The bright end of the block: even a block full of small print is mostly paper there.
                int wanted = count / 10, seen = 0, value = 255;
                while (value > 0 && (seen += histogram[value]) <= wanted)
                {
                    value--;
                }

                grid[(row * across) + column] = value;
            }
        });

        // Block by block, a neighbour's paper wins over a block filled with a heading or a photo.
        double[] widened = new double[grid.Length];
        for (int row = 0; row < down; row++)
        {
            for (int column = 0; column < across; column++)
            {
                double best = 0;
                for (int dy = Math.Max(0, row - 1); dy <= Math.Min(down - 1, row + 1); dy++)
                {
                    for (int dx = Math.Max(0, column - 1); dx <= Math.Min(across - 1, column + 1); dx++)
                    {
                        best = Math.Max(best, grid[(dy * across) + dx]);
                    }
                }

                widened[(row * across) + column] = best;
            }
        }

        double[] sorted = (double[])widened.Clone();
        Array.Sort(sorted);
        double floor = sorted[sorted.Length / 2] * DarkestPaper;

        double[] soft = new double[widened.Length];
        for (int row = 0; row < down; row++)
        {
            for (int column = 0; column < across; column++)
            {
                double sum = 0;
                int count = 0;
                for (int dy = Math.Max(0, row - 1); dy <= Math.Min(down - 1, row + 1); dy++)
                {
                    for (int dx = Math.Max(0, column - 1); dx <= Math.Min(across - 1, column + 1); dx++)
                    {
                        sum += Math.Max(floor, widened[(dy * across) + dx]);
                        count++;
                    }
                }

                soft[(row * across) + column] = sum / count;
            }
        }

        return soft;
    }

    private static void BlackAndWhite(byte[] pixels, int width, int height)
    {
        var histogram = new int[256];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            histogram[Luma(pixels, i)]++;
        }

        // Clamped because a nearly blank page has no real split, and its "best" one would turn paper grain into specks.
        int threshold = Math.Clamp(Otsu(histogram, pixels.Length / 4), 96, 200);
        bool[] ink = new bool[width * height];
        for (int i = 0; i < ink.Length; i++)
        {
            ink[i] = Luma(pixels, i * 4) < threshold;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;
                bool black = ink[at] && HasInkNearby(ink, width, height, x, y);
                byte value = black ? (byte)0 : (byte)255;
                int to = at * 4;
                pixels[to] = pixels[to + 1] = pixels[to + 2] = value;
            }
        }
    }

    /// <summary>A black pixel with nothing black around it is dust, not ink.</summary>
    private static bool HasInkNearby(bool[] ink, int width, int height, int x, int y)
    {
        for (int dy = Math.Max(0, y - 1); dy <= Math.Min(height - 1, y + 1); dy++)
        {
            for (int dx = Math.Max(0, x - 1); dx <= Math.Min(width - 1, x + 1); dx++)
            {
                if ((dx != x || dy != y) && ink[(dy * width) + dx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * (double)histogram[i];
        }

        double sumBelow = 0, best = -1;
        int below = 0, threshold = 128;
        for (int i = 0; i < 256; i++)
        {
            below += histogram[i];
            int above = total - below;
            if (below == 0 || above == 0)
            {
                continue;
            }

            sumBelow += i * (double)histogram[i];
            double difference = (sumBelow / below) - ((sum - sumBelow) / above);
            double between = (double)below * above * difference * difference;
            if (between > best)
            {
                best = between;
                threshold = i;
            }
        }

        return threshold;
    }

    // ================================================================= detail

    /// <summary>Averages each pixel with neighbours of a similar value only, so grain goes but the edges of letters stay.</summary>
    private static byte[] Smooth(byte[] pixels, int width, int height, int strength)
    {
        int radius = strength > 50 ? 2 : 1;
        int similar = 8 + (Math.Min(strength, 100) * 32 / 100);
        byte[] output = new byte[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int top = Math.Max(0, y - radius), bottom = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius), right = Math.Min(width - 1, x + radius);
                int to = ((y * width) + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int centre = pixels[to + channel], sum = 0, count = 0;
                    for (int ny = top; ny <= bottom; ny++)
                    {
                        int at = (((ny * width) + left) * 4) + channel;
                        for (int nx = left; nx <= right; nx++, at += 4)
                        {
                            int value = pixels[at];
                            if (Math.Abs(value - centre) <= similar)
                            {
                                sum += value;
                                count++;
                            }
                        }
                    }

                    output[to + channel] = (byte)(sum / count);
                }

                output[to + 3] = 255;
            }
        });

        return output;
    }

    /// <summary>Unsharp mask: pushes each pixel away from a blurred copy. Tiny differences are left alone, or it would sharpen the grain.</summary>
    private static byte[] Sharpen(byte[] pixels, int width, int height, int strength)
    {
        const int Grain = 4;
        int radius = Math.Max(1, Math.Max(width, height) / 1500);
        double amount = Math.Min(strength, 100) * 0.015;
        byte[] blurred = BoxBlur(pixels, width, height, radius);
        byte[] output = new byte[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int at = y * width * 4;
            for (int x = 0; x < width; x++, at += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int value = pixels[at + channel], difference = value - blurred[at + channel];
                    output[at + channel] = Math.Abs(difference) < Grain ? (byte)value : (byte)Math.Clamp(value + (difference * amount), 0, 255);
                }

                output[at + 3] = 255;
            }
        });

        return output;
    }

    private static byte[] BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        byte[] across = new byte[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int row = y * width * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int x = -radius; x <= radius; x++)
                {
                    sum += pixels[row + (Math.Clamp(x, 0, width - 1) * 4) + channel];
                }

                for (int x = 0; x < width; x++)
                {
                    across[row + (x * 4) + channel] = (byte)(sum / ((2 * radius) + 1));
                    sum += pixels[row + (Math.Min(x + radius + 1, width - 1) * 4) + channel] - pixels[row + (Math.Max(x - radius, 0) * 4) + channel];
                }
            }
        });

        byte[] output = new byte[pixels.Length];
        int stride = width * 4;
        Parallel.For(0, width, x =>
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int column = (x * 4) + channel, sum = 0;
                for (int y = -radius; y <= radius; y++)
                {
                    sum += across[(Math.Clamp(y, 0, height - 1) * stride) + column];
                }

                for (int y = 0; y < height; y++)
                {
                    output[(y * stride) + column] = (byte)(sum / ((2 * radius) + 1));
                    sum += across[(Math.Min(y + radius + 1, height - 1) * stride) + column] - across[(Math.Max(y - radius, 0) * stride) + column];
                }
            }
        });

        return output;
    }

    // ================================================================= straightening

    /// <summary>
    /// How far lines of text are tilted, in degrees. At the true angle, rows of pixels alternate sharply
    /// between full (a line of text) and empty (the gap), so the angle with the sharpest pattern wins.
    /// Returns 0 when nothing looks like lines of text, so photos are left straight.
    /// </summary>
    internal static double FindSkew(byte[] pixels, int width, int height)
    {
        const int Side = 800;
        int step = Math.Max(1, Math.Max(width, height) / Side);
        int w = width / step, h = height / step;
        if (w < 16 || h < 16)
        {
            return 0;
        }

        var histogram = new int[256];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            histogram[Luma(pixels, i)]++;
        }

        int threshold = Otsu(histogram, pixels.Length / 4);
        var ink = new List<(int X, int Y)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Luma(pixels, ((y * step * width) + (x * step)) * 4) < threshold)
                {
                    ink.Add((x, y));
                }
            }
        }

        double share = (double)ink.Count / (w * h);
        if (share < 0.002 || share > 0.5)
        {
            return 0;
        }

        double level = Score(0), best = level, angle = 0;
        for (double a = -LargestSkew; a <= LargestSkew; a += 0.25)
        {
            double score = Score(a);
            if (score > best)
            {
                best = score;
                angle = a;
            }
        }

        double centre = angle;
        for (double a = centre - 0.2; a <= centre + 0.2; a += 0.05)
        {
            double score = Score(a);
            if (score > best)
            {
                best = score;
                angle = a;
            }
        }

        // Must be clearly better than leaving it, or it's only noise agreeing with itself.
        return best > level * 1.1 ? angle : 0;

        double Score(double degrees)
        {
            double slope = Math.Tan(degrees * Math.PI / 180);
            int margin = (int)(w * Math.Abs(slope)) + 2;
            int[] rows = new int[h + (2 * margin)];
            foreach (var (x, y) in ink)
            {
                rows[y + margin + (int)Math.Round(x * slope)]++;
            }

            double total = 0;
            for (int i = 1; i < rows.Length; i++)
            {
                double difference = rows[i] - rows[i - 1];
                total += difference * difference;
            }

            return total;
        }
    }

    /// <summary>Turns the picture by the tilt found. Corners that come in from outside take the colour of the picture's edge.</summary>
    private static byte[] Rotate(byte[] pixels, int width, int height, double degrees)
    {
        double sin = Math.Sin(degrees * Math.PI / 180), cos = Math.Cos(degrees * Math.PI / 180);
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        var edge = EdgeColour(pixels, width, height);
        byte[] output = new byte[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int to = y * width * 4;
            double dy = y - cy;
            for (int x = 0; x < width; x++, to += 4)
            {
                double dx = x - cx;
                double sx = cx + (cos * dx) + (sin * dy), sy = cy - (sin * dx) + (cos * dy);
                output[to + 3] = 255;
                if (sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1)
                {
                    output[to] = edge.B;
                    output[to + 1] = edge.G;
                    output[to + 2] = edge.R;
                    continue;
                }

                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                double fx = sx - x0, fy = sy - y0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double top = (pixels[(((y0 * width) + x0) * 4) + channel] * (1 - fx)) + (pixels[(((y0 * width) + x1) * 4) + channel] * fx);
                    double bottom = (pixels[(((y1 * width) + x0) * 4) + channel] * (1 - fx)) + (pixels[(((y1 * width) + x1) * 4) + channel] * fx);
                    output[to + channel] = (byte)((top * (1 - fy)) + (bottom * fy));
                }
            }
        });

        return output;
    }

    private static (byte B, byte G, byte R) EdgeColour(byte[] pixels, int width, int height)
    {
        var values = new List<(int Luma, int At)>((2 * width) + (2 * height));
        for (int x = 0; x < width; x++)
        {
            values.Add((Luma(pixels, x * 4), x * 4));
            values.Add((Luma(pixels, ((height - 1) * width * 4) + (x * 4)), ((height - 1) * width * 4) + (x * 4)));
        }

        for (int y = 0; y < height; y++)
        {
            values.Add((Luma(pixels, y * width * 4), y * width * 4));
            values.Add((Luma(pixels, ((y * width) + width - 1) * 4), ((y * width) + width - 1) * 4));
        }

        values.Sort();
        int middle = values[values.Count / 2].At;
        return (pixels[middle], pixels[middle + 1], pixels[middle + 2]);
    }
}
