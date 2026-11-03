namespace Papery.Core.Pages;

/// <summary>
/// Puts double-sided paper back in order after a scanner that only reads one side: the fronts go through first,
/// then the stack is turned over and the backs go through, which usually brings them out last page first.
/// </summary>
public static class ScanOrder
{
    /// <summary>
    /// Fronts and backs from two files, shuffled together: front 1, back 1, front 2, back 2. When one file has more
    /// pages, the rest of it follows at the end. Each entry is (file, page): file 0 holds the fronts, file 1 the backs.
    /// </summary>
    /// <param name="backsReversed">The backs were scanned last page first, as they are after turning the stack over.</param>
    public static IReadOnlyList<(int File, int Page)> Shuffle(int fronts, int backs, bool backsReversed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fronts);
        ArgumentOutOfRangeException.ThrowIfNegative(backs);
        var order = new List<(int, int)>(fronts + backs);
        for (int i = 0; i < Math.Max(fronts, backs); i++)
        {
            if (i < fronts)
            {
                order.Add((0, i));
            }

            if (i < backs)
            {
                order.Add((1, backsReversed ? backs - 1 - i : i));
            }
        }

        return order;
    }

    /// <summary>
    /// The same for one file holding all the fronts, then all the backs. With an odd count, the extra page is taken
    /// as a front (the last sheet had nothing on its back, or the back wasn't scanned). Returns where each page goes:
    /// result[i] is the page that belongs at position i.
    /// </summary>
    public static IReadOnlyList<int> Shuffle(int pageCount, bool backsReversed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        int fronts = (pageCount + 1) / 2;
        return Shuffle(fronts, pageCount - fronts, backsReversed).Select(p => p.File == 0 ? p.Page : fronts + p.Page).ToArray();
    }
}
