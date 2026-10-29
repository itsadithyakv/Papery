using System.Globalization;

namespace Papery.Pdf.Editing;

/// <summary>One file a split will produce.</summary>
/// <param name="Name">What goes after the original name, e.g. "part 03" or "02 Methods".</param>
/// <param name="Pages">0-based pages of the original, in order.</param>
public sealed record SplitPart(string Name, IReadOnlyList<int> Pages);

/// <summary>Works out the parts for each way of splitting. Pure, so it's unit-tested.</summary>
public static class SplitPlanner
{
    /// <summary>Consecutive chunks of <paramref name="size"/> pages (the last may be shorter). Size 1 = every page on its own.</summary>
    public static IReadOnlyList<SplitPart> EveryNPages(int pageCount, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        int count = (pageCount + size - 1) / size;
        return Enumerable.Range(0, count)
            .Select(i => new SplitPart(PartName(i, count), Enumerable.Range(i * size, Math.Min(size, pageCount - (i * size))).ToArray()))
            .ToArray();
    }

    /// <summary>One part per group the user typed ("1-3, 4-10" = two parts).</summary>
    public static IReadOnlyList<SplitPart> FromGroups(IReadOnlyList<IReadOnlyList<int>> groups) =>
        groups.Select((pages, i) => new SplitPart(PartName(i, groups.Count), pages)).ToArray();

    /// <summary>
    /// One part per chapter: each top-level bookmark runs until the next one starts. When the
    /// whole outline sits under one title bookmark, its children are used instead. Pages before
    /// the first chapter become a "Start" part.
    /// </summary>
    public static IReadOnlyList<SplitPart> ByBookmarks(IReadOnlyList<PdfOutlineItem> outline, int pageCount)
    {
        var level = outline;
        while (Usable(level).Count() == 1 && Usable(level.First(i => i.PageIndex.HasValue).Children).Count() >= 2)
        {
            level = level.First(i => i.PageIndex.HasValue).Children;
        }

        // Chapters in page order; two bookmarks on the same page share one part (the first title wins).
        var starts = Usable(level)
            .Where(i => i.PageIndex < pageCount)
            .GroupBy(i => i.PageIndex!.Value)
            .Select(g => (Page: g.Key, g.First().Title))
            .OrderBy(s => s.Page)
            .ToList();
        if (starts.Count == 0)
        {
            return [];
        }

        if (starts[0].Page > 0)
        {
            starts.Insert(0, (0, "Start"));
        }

        int digits = starts.Count.ToString(CultureInfo.InvariantCulture).Length;
        var parts = new List<SplitPart>();
        for (int i = 0; i < starts.Count; i++)
        {
            int from = starts[i].Page, to = i + 1 < starts.Count ? starts[i + 1].Page : pageCount;
            string number = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(2, digits), '0');
            parts.Add(new SplitPart($"{number} {starts[i].Title}", Enumerable.Range(from, to - from).ToArray()));
        }

        return parts;

        static IEnumerable<PdfOutlineItem> Usable(IReadOnlyList<PdfOutlineItem> items) => items.Where(i => i.PageIndex.HasValue);
    }

    /// <summary>"Report - part 03.pdf": safe for Windows, short enough for any folder.</summary>
    public static string FileName(string originalName, SplitPart part)
    {
        string name = $"{Path.GetFileNameWithoutExtension(originalName)} - {part.Name}";
        return SafeFileName(name, maxLength: 120) + ".pdf";
    }

    /// <summary>The path itself if free, otherwise "name (2).pdf", "name (3).pdf"... Never overwrites.</summary>
    public static string UniquePath(string path) => SafeFile.MakeUnique(path);

    /// <summary>Replaces characters Windows doesn't allow in file names and trims the length.</summary>
    public static string SafeFileName(string name, int maxLength = 120)
    {
        // Read naturally first ("Chapter 1: Intro" -> "Chapter 1 - Intro"), then replace anything else Windows refuses.
        name = name.Replace(": ", " - ", StringComparison.Ordinal).Replace(':', '-').Replace('/', '-').Replace('\\', '-');
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        string safe = new string(chars).Trim().TrimEnd('.');
        if (safe.Length > maxLength)
        {
            safe = safe[..maxLength].TrimEnd();
        }

        return safe.Length == 0 ? "part" : safe;
    }

    /// <summary>
    /// Bookmarks for a new file: <paramref name="pageMap"/> turns an original page into its page in
    /// the new file, or null if that page isn't in it. Bookmarks to missing pages keep their place
    /// only if something inside them survives.
    /// </summary>
    public static IReadOnlyList<PdfOutlineItem> RemapOutline(IReadOnlyList<PdfOutlineItem> outline, Func<int, int?> pageMap)
    {
        var result = new List<PdfOutlineItem>();
        foreach (var item in outline)
        {
            int? page = item.PageIndex is int p ? pageMap(p) : null;
            var children = RemapOutline(item.Children, pageMap);
            if (page.HasValue || children.Count > 0)
            {
                result.Add(new PdfOutlineItem(item.Title, page, children));
            }
        }

        return result;
    }

    private static string PartName(int index, int count)
    {
        int digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        return "part " + (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
