using System.Globalization;

namespace Papery.Pdf.Editing;

/// <summary>
/// Suggests bookmarks from a document's headings: lines set clearly larger than the body text. The biggest size is
/// the top level, and up to three levels are kept. Lines repeated at the same place on many pages (running headers),
/// page numbers and long lines are left out. It's a suggestion to review, since a PDF doesn't say what a heading is.
/// </summary>
public static class HeadingOutline
{
    private const double HeadingScale = 1.15;  // this much larger than body text to count
    private const int MaxLevels = 3;
    private const int MaxTitleLength = 120;
    private const int MaxWords = 16;
    private const int MaxHeadings = 1_000;

    private sealed record Line(int Page, string Text, double Top, double Bottom, double Size, double Left);

    /// <summary>The headings found, as a tree. <paramref name="pages"/> holds each page's words in reading order.</summary>
    public static IReadOnlyList<PdfOutlineItem> Find(IReadOnlyList<IReadOnlyList<PdfWord>> pages)
    {
        var lines = new List<Line>();
        for (int page = 0; page < pages.Count; page++)
        {
            lines.AddRange(Lines(page, pages[page]));
        }

        if (lines.Count == 0)
        {
            return [];
        }

        double body = BodySize(lines);
        var repeated = Repeated(lines, pages.Count);
        var headings = Join(lines.Where(l => l.Size >= body * HeadingScale && LooksLikeHeading(l.Text) && !repeated.Contains(Key(l))).ToList());
        if (headings.Count == 0)
        {
            return [];
        }

        // Sizes within half a point are the same level; the largest is level 1.
        var sizes = new List<double>();
        foreach (double size in headings.Select(h => h.Size).OrderByDescending(s => s))
        {
            if (sizes.Count == 0 || sizes[^1] - size > 0.5)
            {
                sizes.Add(size);
            }
        }

        int LevelOf(double size)
        {
            int level = sizes.FindIndex(s => s - size <= 0.5);
            return Math.Min(level < 0 ? sizes.Count - 1 : level, MaxLevels - 1);
        }

        return Tree(headings.Take(MaxHeadings).Select(h => (h, LevelOf(h.Size))).ToList());
    }

    /// <summary>Words grouped into lines: a new line starts when a word sits lower or goes back to the left.</summary>
    private static IEnumerable<Line> Lines(int page, IReadOnlyList<PdfWord> words)
    {
        var current = new List<PdfWord>();
        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                double middle = word.Area.Y + (word.Area.Height / 2);
                bool sameRow = middle > last.Area.Y && middle < last.Area.Bottom;
                if (!sameRow || word.Area.X < last.Area.X)
                {
                    yield return Make(page, current);
                    current.Clear();
                }
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            yield return Make(page, current);
        }
    }

    private static Line Make(int page, List<PdfWord> words) => new(
        page,
        string.Join(' ', words.Select(w => w.Text.Trim())).Trim(),
        words.Min(w => w.Area.Y),
        words.Max(w => w.Area.Bottom),
        Math.Round(words.Max(w => (double)w.FontSize) * 2) / 2,
        words.Min(w => w.Area.X));

    /// <summary>The size most of the letters are set in.</summary>
    private static double BodySize(List<Line> lines) =>
        lines.GroupBy(l => l.Size).OrderByDescending(g => g.Sum(l => l.Text.Length)).First().Key;

    private static string Key(Line line) =>
        $"{line.Text.ToUpperInvariant()}|{Math.Round(line.Top * 50).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Text at the same height on at least three pages (and a quarter of them) is a running header or footer.</summary>
    private static HashSet<string> Repeated(List<Line> lines, int pageCount)
    {
        int enough = Math.Max(3, pageCount / 4);
        return lines.GroupBy(Key).Where(g => g.Select(l => l.Page).Distinct().Count() >= enough).Select(g => g.Key).ToHashSet();
    }

    private static bool LooksLikeHeading(string text) =>
        text.Length is >= 2 and <= MaxTitleLength
        && text.Any(char.IsLetter)
        && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxWords;

    /// <summary>A heading set over two lines (same page, same size, right below) becomes one title.</summary>
    private static List<Line> Join(List<Line> headings)
    {
        var joined = new List<Line>();
        foreach (var line in headings)
        {
            if (joined.Count > 0 && joined[^1] is var last && last.Page == line.Page && Math.Abs(last.Size - line.Size) < 0.25
                && line.Top - last.Bottom < (last.Bottom - last.Top) * 0.8 && line.Top >= last.Top
                && last.Text.Length + line.Text.Length < MaxTitleLength)
            {
                joined[^1] = last with { Text = $"{last.Text} {line.Text}", Bottom = line.Bottom };
            }
            else
            {
                joined.Add(line);
            }
        }

        return joined;
    }

    private static List<PdfOutlineItem> Tree(List<(Line Line, int Level)> headings)
    {
        var root = new List<PdfOutlineItem>();
        var open = new List<(int Level, List<PdfOutlineItem> Children)> { (-1, root) };
        foreach (var (line, level) in headings)
        {
            while (open[^1].Level >= level)
            {
                open.RemoveAt(open.Count - 1);
            }

            var children = new List<PdfOutlineItem>();

            // A little above the heading, so it isn't cut off at the top of the window.
            open[^1].Children.Add(new PdfOutlineItem(line.Text, line.Page, children, Math.Max(0, line.Top - 0.02)));
            open.Add((level, children));
        }

        return root;
    }
}
