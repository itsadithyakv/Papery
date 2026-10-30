namespace Papery.Core.Imaging;

/// <summary>A point on a picture, as fractions of its width and height.</summary>
public readonly record struct ScanPoint(double X, double Y);

/// <summary>The four corners of a sheet of paper in a photo.</summary>
public sealed record ScanCorners(ScanPoint TopLeft, ScanPoint TopRight, ScanPoint BottomRight, ScanPoint BottomLeft)
{
    public static ScanCorners WholePicture { get; } = new(new(0, 0), new(1, 0), new(1, 1), new(0, 1));
}

public enum ScanLook
{
    Colour,
    Grey,
    BlackAndWhite,
}

/// <summary>Finds a sheet of paper in a photo, straightens it and cleans it up. No PDF or Windows parts, so it can be tested.</summary>
public static class DocumentScan
{
    private const int DetectionSide = 400;

    // A sheet smaller than this is probably something else; one nearly filling the frame is no help finding edges.
    private const double SmallestSheet = 0.15;
    private const double LargestSheet = 0.98;

    /// <summary>The corners of the largest bright shape, or null when no sheet stands out from what's behind it.</summary>
    public static ScanCorners? FindCorners(byte[] bgra, int width, int height, int stride)
    {
        double step = Math.Max(1, (double)Math.Max(width, height) / DetectionSide);
        int w = Math.Max(1, (int)(width / step)), h = Math.Max(1, (int)(height / step));
        byte[] grey = new byte[w * h];
        var histogram = new int[256];
        for (int y = 0; y < h; y++)
        {
            int row = (int)(y * step) * stride;
            for (int x = 0; x < w; x++)
            {
                int at = row + ((int)(x * step) * 4);
                byte value = Luma(bgra[at], bgra[at + 1], bgra[at + 2]);
                grey[(y * w) + x] = value;
                histogram[value]++;
            }
        }

        int threshold = Otsu(histogram, w * h);
        var sheet = LargestBrightRegion(grey, w, h, threshold);
        if (sheet.Count < SmallestSheet * w * h || sheet.Count > LargestSheet * w * h)
        {
            return null;
        }

        // Each corner is the point furthest in its diagonal direction.
        int tl = sheet[0], tr = sheet[0], br = sheet[0], bl = sheet[0];
        foreach (int p in sheet)
        {
            int x = p % w, y = p / w;
            if (x + y < (tl % w) + (tl / w))
            {
                tl = p;
            }

            if (x + y > (br % w) + (br / w))
            {
                br = p;
            }

            if (x - y > (tr % w) - (tr / w))
            {
                tr = p;
            }

            if (x - y < (bl % w) - (bl / w))
            {
                bl = p;
            }
        }

        ScanPoint Point(int p) => new((double)(p % w) / Math.Max(1, w - 1), (double)(p / w) / Math.Max(1, h - 1));
        return new ScanCorners(Point(tl), Point(tr), Point(br), Point(bl));
    }

    /// <summary>Lifts the sheet out of the photo and squares it up. Returns the new pixels and size.</summary>
    public static (byte[] Pixels, int Width, int Height) Straighten(byte[] bgra, int width, int height, int stride, ScanCorners corners, int maxSide = 4000)
    {
        double Across(ScanPoint a, ScanPoint b) => Math.Sqrt(Math.Pow((a.X - b.X) * width, 2) + Math.Pow((a.Y - b.Y) * height, 2));
        double wide = Math.Max(Across(corners.TopLeft, corners.TopRight), Across(corners.BottomLeft, corners.BottomRight));
        double tall = Math.Max(Across(corners.TopLeft, corners.BottomLeft), Across(corners.TopRight, corners.BottomRight));
        double fit = Math.Min(1, maxSide / Math.Max(1, Math.Max(wide, tall)));
        int outWidth = Math.Max(1, (int)(wide * fit)), outHeight = Math.Max(1, (int)(tall * fit));

        var map = Homography.FromUnitSquare(corners);
        byte[] output = new byte[outWidth * outHeight * 4];
        for (int y = 0; y < outHeight; y++)
        {
            double v = outHeight == 1 ? 0 : (double)y / (outHeight - 1);
            for (int x = 0; x < outWidth; x++)
            {
                double u = outWidth == 1 ? 0 : (double)x / (outWidth - 1);
                var (sx, sy) = map.Apply(u, v);
                Sample(bgra, width, height, stride, sx * (width - 1), sy * (height - 1), output, ((y * outWidth) + x) * 4);
            }
        }

        return (output, outWidth, outHeight);
    }

    /// <summary>Makes a scan look like a clean copy: whiter paper and darker ink, in colour, grey or pure black and white.</summary>
    public static byte[] Enhance(byte[] bgra, int width, int height, int stride, ScanLook look)
    {
        byte[] output = new byte[width * height * 4];
        byte[] grey = new byte[width * height];
        var histogram = new int[256];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * stride) + (x * 4);
                byte value = Luma(bgra[at], bgra[at + 1], bgra[at + 2]);
                grey[(y * width) + x] = value;
                histogram[value]++;
            }
        }

        if (look == ScanLook.BlackAndWhite)
        {
            Threshold(grey, width, height, output);
            return output;
        }

        // Stretches so the paper (the bright end) reaches white and the ink (the dark end) reaches black.
        var (low, high) = Levels(histogram, width * height);
        double scale = 255.0 / Math.Max(1, high - low);
        byte Stretch(byte value) => (byte)Math.Clamp((value - low) * scale, 0, 255);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int from = (y * stride) + (x * 4), to = ((y * width) + x) * 4;
                if (look == ScanLook.Grey)
                {
                    output[to] = output[to + 1] = output[to + 2] = Stretch(grey[(y * width) + x]);
                }
                else
                {
                    output[to] = Stretch(bgra[from]);
                    output[to + 1] = Stretch(bgra[from + 1]);
                    output[to + 2] = Stretch(bgra[from + 2]);
                }

                output[to + 3] = 255;
            }
        }

        return output;
    }

    private static byte Luma(byte b, byte g, byte r) => (byte)(((r * 299) + (g * 587) + (b * 114)) / 1000);

    /// <summary>The brightness that best splits a picture into two groups (Otsu's method).</summary>
    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * histogram[i];
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

            sumBelow += i * histogram[i];
            double meanBelow = sumBelow / below, meanAbove = (sum - sumBelow) / above;
            double between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = i;
            }
        }

        return threshold;
    }

    private static List<int> LargestBrightRegion(byte[] grey, int w, int h, int threshold)
    {
        bool[] seen = new bool[grey.Length];
        List<int> largest = [];
        var queue = new Queue<int>();
        for (int start = 0; start < grey.Length; start++)
        {
            if (seen[start] || grey[start] <= threshold)
            {
                continue;
            }

            var region = new List<int>();
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int p))
            {
                region.Add(p);
                int x = p % w;
                void Visit(int next)
                {
                    if (!seen[next] && grey[next] > threshold)
                    {
                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }

                if (x > 0)
                {
                    Visit(p - 1);
                }

                if (x < w - 1)
                {
                    Visit(p + 1);
                }

                if (p >= w)
                {
                    Visit(p - w);
                }

                if (p < grey.Length - w)
                {
                    Visit(p + w);
                }
            }

            if (region.Count > largest.Count)
            {
                largest = region;
            }
        }

        return largest;
    }

    private static void Sample(byte[] bgra, int width, int height, int stride, double x, double y, byte[] output, int to)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double fx = x - x0, fy = y - y0;
        for (int c = 0; c < 4; c++)
        {
            double top = (bgra[(y0 * stride) + (x0 * 4) + c] * (1 - fx)) + (bgra[(y0 * stride) + (x1 * 4) + c] * fx);
            double bottom = (bgra[(y1 * stride) + (x0 * 4) + c] * (1 - fx)) + (bgra[(y1 * stride) + (x1 * 4) + c] * fx);
            output[to + c] = (byte)((top * (1 - fy)) + (bottom * fy));
        }
    }

    /// <summary>The darkest and brightest values that matter, ignoring a little at each end (specks and glare).</summary>
    private static (int Low, int High) Levels(int[] histogram, int total)
    {
        int ignore = total / 200, seen = 0, low = 0, high = 255;
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

    /// <summary>Compares each pixel to the area around it, so a shadow across the page doesn't turn black.</summary>
    private static void Threshold(byte[] grey, int width, int height, byte[] output)
    {
        long[] sums = new long[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            long row = 0;
            for (int x = 0; x < width; x++)
            {
                row += grey[(y * width) + x];
                sums[((y + 1) * (width + 1)) + x + 1] = sums[(y * (width + 1)) + x + 1] + row;
            }
        }

        int radius = Math.Max(7, Math.Max(width, height) / 40);
        for (int y = 0; y < height; y++)
        {
            int top = Math.Max(0, y - radius), bottom = Math.Min(height, y + radius + 1);
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius), right = Math.Min(width, x + radius + 1);
                long total = sums[(bottom * (width + 1)) + right] - sums[(top * (width + 1)) + right] - sums[(bottom * (width + 1)) + left] + sums[(top * (width + 1)) + left];
                double mean = (double)total / ((right - left) * (bottom - top));
                byte value = grey[(y * width) + x] < mean * 0.85 ? (byte)0 : (byte)255;
                int to = ((y * width) + x) * 4;
                output[to] = output[to + 1] = output[to + 2] = value;
                output[to + 3] = 255;
            }
        }
    }

    /// <summary>Maps the unit square onto four corners, keeping straight lines straight.</summary>
    private readonly record struct Homography(double A, double B, double C, double D, double E, double F, double G, double H)
    {
        public static Homography FromUnitSquare(ScanCorners q)
        {
            double x0 = q.TopLeft.X, y0 = q.TopLeft.Y, x1 = q.TopRight.X, y1 = q.TopRight.Y;
            double x2 = q.BottomRight.X, y2 = q.BottomRight.Y, x3 = q.BottomLeft.X, y3 = q.BottomLeft.Y;
            double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
            if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
            {
                return new Homography(x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0);
            }

            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = (dx1 * dy2) - (dx2 * dy1);
            double g = ((sx * dy2) - (dx2 * sy)) / den, h = ((dx1 * sy) - (sx * dy1)) / den;
            return new Homography(x1 - x0 + (g * x1), x3 - x0 + (h * x3), x0, y1 - y0 + (g * y1), y3 - y0 + (h * y3), y0, g, h);
        }

        public (double X, double Y) Apply(double u, double v)
        {
            double w = (G * u) + (H * v) + 1;
            return (((A * u) + (B * v) + C) / w, ((D * u) + (E * v) + F) / w);
        }
    }
}
