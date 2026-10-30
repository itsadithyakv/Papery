namespace Papery.Core.Imaging;

/// <summary>A rectangle as fractions of an image (0,0 top-left; 1,1 bottom-right).</summary>
public readonly record struct FractionRect(double X, double Y, double Width, double Height);

/// <summary>What a printed form element looks like it's for.</summary>
public enum DetectedFieldKind
{
    /// <summary>A line to write on: the field sits just above it.</summary>
    Line,

    /// <summary>A small square to tick.</summary>
    CheckBox,

    /// <summary>A larger empty box to write in.</summary>
    TextBox,
}

public readonly record struct DetectedField(DetectedFieldKind Kind, FractionRect Area);

/// <summary>
/// Looks at a rendered page (BGRA pixels) to find where the content is and how crooked a scan is.
/// Plain loops over bytes, so it runs anywhere and is unit-tested with drawn test images.
/// </summary>
public static class PageImageAnalysis
{
    /// <summary>
    /// The smallest rectangle holding everything that isn't paper, or null for a blank page.
    /// </summary>
    /// <remarks>
    /// Works for clean PDFs and for scans: ink is judged against the page's own paper colour (scans
    /// are grey, not white), dark areas joined to the edge of the image are scanner borders, not content (a flood fill
    /// from the edges removes them; text never touches the edge). Rows and columns are judged a few pixels at a time, so lone specks of dust don't count
    /// while a line of text, a rule or a small page number does.
    /// </remarks>
    /// <param name="paperLuma">Brightness (0-255) above which a pixel always counts as paper.</param>
    public static FractionRect? ContentBounds(byte[] bgra, int width, int height, int stride, int paperLuma = 235)
    {
        // The paper is the most common bright shade; ink must be clearly darker than it.
        var histogram = new int[256];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                histogram[Luma(bgra, (y * stride) + (x * 4))]++;
            }
        }

        int paper = 128;
        for (int v = 129; v < 256; v++)
        {
            if (histogram[v] > histogram[paper])
            {
                paper = v;
            }
        }

        // Two levels: anything a little darker than paper (shadows fade gradually, so they need a
        // soft level to stay in one piece) and real ink, clearly darker.
        int ink = Math.Min(paperLuma, paper - 90), shade = Math.Min(paperLuma, paper - 25);
        var luma = new byte[width * height];
        var shaded = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int v = Luma(bgra, row + (x * 4));
                luma[(y * width) + x] = (byte)v;
                shaded[(y * width) + x] = v < shade;
            }
        }

        var border = BorderShadows(shaded, width, height);
        var inked = new bool[width * height];
        for (int i = 0; i < inked.Length; i++)
        {
            inked[i] = luma[i] < ink && !border[i];
        }

        RemoveSpecks(inked, width, height, minArea: Math.Max(6, width * height / 150_000));
        var rows = new int[height];
        var columns = new int[width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                if (inked[i])
                {
                    rows[y]++;
                    columns[x]++;
                }
            }
        }

        int top = Edge(rows, fromStart: true), bottom = Edge(rows, fromStart: false);
        int left = Edge(columns, fromStart: true), right = Edge(columns, fromStart: false);
        if (top < 0 || left < 0 || bottom < top || right < left)
        {
            return null;
        }

        return new FractionRect(
            (double)left / width,
            (double)top / height,
            (double)(right - left + 1) / width,
            (double)(bottom - top + 1) / height);
    }

    /// <summary>
    /// Finds what looks fillable on a printed form: writing lines, tick boxes and empty boxes.
    /// </summary>
    /// <remarks>
    /// Works from straight runs of dark pixels. A long horizontal run with nothing connected under or
    /// over it is a writing line. Four runs meeting at corners are a box: small and square means a tick box,
    /// large and empty means a text box. A box with something inside it, like a table's frame, is skipped,
    /// and the lines of a table are skipped because vertical lines cross them.
    /// </remarks>
    public static IReadOnlyList<DetectedField> FindFormFields(byte[] bgra, int width, int height, int stride)
    {
        var dark = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                dark[(y * width) + x] = Luma(bgra, (y * stride) + (x * 4)) < 215; // thin printed lines render pale
            }
        }

        int minLine = width / 12, minSide = Math.Max(8, width / 110), maxTickSide = width / 28;
        var horizontal = new List<(int X1, int X2, int Y)>();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                if (!dark[(y * width) + x])
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < width && dark[(y * width) + x])
                {
                    x++;
                }

                if (x - start >= minSide)
                {
                    horizontal.Add((start, x - 1, y));
                }
            }
        }

        // A line a few pixels thick shows up once per row: keep one run for each.
        horizontal = MergeThick(horizontal);
        bool VerticalAt(int x, int y1, int y2)
        {
            for (int y = y1; y <= y2; y++)
            {
                bool any = false;
                for (int dx = -1; dx <= 1 && !any; dx++)
                {
                    any = (uint)(x + dx) < (uint)width && dark[(y * width) + x + dx];
                }

                if (!any)
                {
                    return false;
                }
            }

            return true;
        }

        bool Crossed(int x1, int x2, int y)
        {
            // Something leaving the line up or down, away from its ends: a table or a grid.
            int reach = Math.Max(4, height / 200);
            for (int x = x1 + 3; x <= x2 - 3; x++)
            {
                bool up = y - reach >= 0 && VerticalAt(x, y - reach, y);
                bool down = y + reach < height && VerticalAt(x, y, y + reach);
                if (up || down)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsEmpty(int x1, int y1, int x2, int y2)
        {
            int margin = 3, ink = 0, area = Math.Max(1, (x2 - x1 - (2 * margin)) * (y2 - y1 - (2 * margin)));
            for (int y = y1 + margin; y < y2 - margin; y++)
            {
                for (int x = x1 + margin; x < x2 - margin; x++)
                {
                    ink += dark[(y * width) + x] ? 1 : 0;
                }
            }

            return ink * 200 < area; // under half a percent: specks at most
        }

        var found = new List<DetectedField>();
        var usedAsBox = new HashSet<(int X1, int X2, int Y)>();

        // Boxes: a top edge, a matching bottom edge, and straight sides joining them.
        foreach (var top in horizontal)
        {
            foreach (var bottom in horizontal)
            {
                int side = bottom.Y - top.Y;
                if (side < minSide || usedAsBox.Contains(top) || usedAsBox.Contains(bottom)
                    || Math.Abs(top.X1 - bottom.X1) > 3 || Math.Abs(top.X2 - bottom.X2) > 3
                    || !VerticalAt(top.X1 + 1, top.Y, bottom.Y) || !VerticalAt(top.X2 - 1, top.Y, bottom.Y))
                {
                    continue;
                }

                int w = top.X2 - top.X1;
                bool tick = w <= maxTickSide && side <= maxTickSide && Math.Abs(w - side) <= Math.Max(3, w / 4);
                usedAsBox.Add(top);
                usedAsBox.Add(bottom);
                if (!IsEmpty(top.X1, top.Y, top.X2, bottom.Y))
                {
                    continue; // a frame around content (like a table)
                }

                if (tick || w > maxTickSide)
                {
                    found.Add(new DetectedField(
                        tick ? DetectedFieldKind.CheckBox : DetectedFieldKind.TextBox,
                        new FractionRect((double)top.X1 / width, (double)top.Y / height, (double)w / width, (double)side / height)));
                }
            }
        }

        // Writing lines: long, free-standing, with room above.
        int lineHeight = Math.Max(10, height / 48);
        foreach (var line in horizontal)
        {
            if (usedAsBox.Contains(line) || line.X2 - line.X1 < minLine || Crossed(line.X1, line.X2, line.Y) || line.Y < lineHeight)
            {
                continue;
            }

            if (!IsEmpty(line.X1, line.Y - lineHeight, line.X2, line.Y))
            {
                continue; // a rule under a heading, or an underline beneath words
            }

            found.Add(new DetectedField(
                DetectedFieldKind.Line,
                new FractionRect((double)line.X1 / width, (double)(line.Y - lineHeight) / height, (double)(line.X2 - line.X1) / width, (double)lineHeight / height)));
        }

        return found.OrderBy(f => Math.Round(f.Area.Y, 2)).ThenBy(f => f.Area.X).ToList();

        static List<(int X1, int X2, int Y)> MergeThick(List<(int X1, int X2, int Y)> runs)
        {
            var kept = new List<(int X1, int X2, int Y)>();
            foreach (var run in runs)
            {
                int index = kept.FindLastIndex(k => run.Y - k.Y <= 4 && Math.Abs(k.X1 - run.X1) <= 3 && Math.Abs(k.X2 - run.X2) <= 3);
                if (index >= 0)
                {
                    kept[index] = run; // the same line, one row lower
                }
                else
                {
                    kept.Add(run);
                }
            }

            return kept;
        }
    }

    /// <summary>
    /// How many degrees to turn the page clockwise so its lines of text run straight (negative =
    /// anticlockwise). 0 when there's too little ink to tell, or the page is already straight.
    /// </summary>
    /// <remarks>
    /// Projection profiles: lines of text are straight exactly when, viewed along the right angle,
    /// the ink piles up into sharp bands. For each candidate angle the dark pixels are projected onto
    /// that angle and the bands' sharpness is scored (sum of squared counts); the best angle wins.
    /// A coarse pass over ±<paramref name="maxDegrees"/> is refined to a few hundredths of a degree.
    /// </remarks>
    public static double SkewDegrees(byte[] bgra, int width, int height, int stride, double maxDegrees = 10)
    {
        // Sample dark pixels (enough for a stable answer, few enough to be quick).
        var xs = new List<float>();
        var ys = new List<float>();
        int step = Math.Max(1, (int)Math.Sqrt((double)width * height / 400_000));
        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                if (Luma(bgra, (y * stride) + (x * 4)) < 128)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        if (xs.Count < 200)
        {
            return 0;
        }

        double best = 0, bestScore = Score(0);
        for (double a = -maxDegrees; a <= maxDegrees; a += 0.25)
        {
            Consider(a);
        }

        double coarse = best;
        for (double a = coarse - 0.25; a <= coarse + 0.25; a += 0.02)
        {
            Consider(a);
        }

        // The tilt found is how far the lines lean clockwise; straightening turns them back.
        double result = -Math.Round(best, 2);
        return Math.Abs(result) < 0.05 ? 0 : result;

        void Consider(double angle)
        {
            double score = Score(angle);
            if (score > bestScore)
            {
                (best, bestScore) = (angle, score);
            }
        }

        double Score(double degrees)
        {
            double radians = degrees * Math.PI / 180;
            double sin = Math.Sin(radians), cos = Math.Cos(radians);
            int bins = height + width + 2;
            var counts = new int[bins];
            int offset = width; // projections can be negative by up to the width
            for (int i = 0; i < xs.Count; i++)
            {
                int bin = (int)((ys[i] * cos) - (xs[i] * sin)) + offset;
                if ((uint)bin < (uint)bins)
                {
                    counts[bin]++;
                }
            }

            double sum = 0;
            foreach (int c in counts)
            {
                sum += (double)c * c;
            }

            return sum;
        }
    }

    /// <summary>
    /// The first (or last) row or column that is real content, or -1 if none. Counts are summed over
    /// a small window: a speck is one or two pixels in one place, real content repeats over several.
    /// </summary>
    private static int Edge(int[] counts, bool fromStart)
    {
        const int Half = 2; // a window of 5 rows or columns
        var window = new int[counts.Length];
        int max = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            int sum = 0;
            for (int j = Math.Max(0, i - Half); j <= Math.Min(counts.Length - 1, i + Half); j++)
            {
                sum += counts[j];
            }

            window[i] = sum;
            max = Math.Max(max, sum);
        }

        int enough = Math.Max(4, max / 100);
        for (int n = 0; n < counts.Length; n++)
        {
            int i = fromStart ? n : counts.Length - 1 - n;
            if (window[i] < enough)
            {
                continue;
            }

            // Found content near here: step to the exact first (or last) inked row in the window.
            for (int k = Math.Max(0, i - Half); k <= Math.Min(counts.Length - 1, i + Half); k++)
            {
                int j = fromStart ? k : i + Half - (k - Math.Max(0, i - Half));
                if ((uint)j < (uint)counts.Length && counts[j] > 0)
                {
                    return j;
                }
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Clears blobs of ink smaller than <paramref name="minArea"/> pixels: dust and sensor specks.
    /// (A full stop is about that small too, but losing one never changes where the content is.)
    /// </summary>
    private static void RemoveSpecks(bool[] inked, int width, int height, int minArea)
    {
        var seen = new bool[inked.Length];
        var blob = new List<int>();
        var pending = new Stack<int>();
        for (int start = 0; start < inked.Length; start++)
        {
            if (!inked[start] || seen[start])
            {
                continue;
            }

            blob.Clear();
            pending.Push(start);
            seen[start] = true;
            while (pending.Count > 0)
            {
                int i = pending.Pop();
                blob.Add(i);
                int x = i % width, y = i / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                        {
                            int n = (ny * width) + nx;
                            if (inked[n] && !seen[n])
                            {
                                seen[n] = true;
                                pending.Push(n);
                            }
                        }
                    }
                }

                if (blob.Count >= minArea)
                {
                    break; // big enough to be real; the rest of it is kept as is
                }
            }

            pending.Clear();
            if (blob.Count < minArea)
            {
                foreach (int i in blob)
                {
                    inked[i] = false;
                }
            }
        }
    }

    /// <summary>Marks every shaded pixel connected to the image's edge (scanner shadows and borders).</summary>
    private static bool[] BorderShadows(bool[] shaded, int width, int height)
    {
        var border = new bool[shaded.Length];
        var pending = new Stack<int>();
        for (int x = 0; x < width; x++)
        {
            pending.Push(x);
            pending.Push(((height - 1) * width) + x);
        }

        for (int y = 0; y < height; y++)
        {
            pending.Push(y * width);
            pending.Push((y * width) + width - 1);
        }

        while (pending.Count > 0)
        {
            int i = pending.Pop();
            if (!shaded[i] || border[i])
            {
                continue;
            }

            border[i] = true;
            int x = i % width, y = i / width;
            if (x > 0)
            {
                pending.Push(i - 1);
            }

            if (x < width - 1)
            {
                pending.Push(i + 1);
            }

            if (y > 0)
            {
                pending.Push(i - width);
            }

            if (y < height - 1)
            {
                pending.Push(i + width);
            }
        }

        return border;
    }

    private static int Luma(byte[] bgra, int i) => ((bgra[i + 2] * 299) + (bgra[i + 1] * 587) + (bgra[i] * 114)) / 1000;
}
