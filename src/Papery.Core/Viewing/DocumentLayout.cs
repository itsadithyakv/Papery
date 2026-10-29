namespace Papery.Core.Viewing;

/// <summary>How pages are arranged in the viewer.</summary>
public enum ViewMode
{
    /// <summary>One page at a time; Page Down moves to the next.</summary>
    SinglePage,

    /// <summary>All pages in one long scrolling column.</summary>
    Continuous,

    /// <summary>Pages side by side in pairs: 1-2, 3-4, ...</summary>
    TwoPage,

    /// <summary>Like a book: the cover alone, then 2-3, 4-5, ...</summary>
    Book,
}

/// <summary>A rectangle in layout units (DIPs at 100% zoom).</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IntersectsVertically(double top, double bottom) => Bottom >= top && Y <= bottom;
}

/// <summary>
/// Where every page sits in the scrollable area, for a given view mode. Pure maths with no UI,
/// so it is fast for 10,000 pages and covered by unit tests.
/// </summary>
/// <remarks>
/// Units are device-independent pixels at 100% zoom (1 PDF point = 96/72 DIP). The viewer
/// applies zoom on top, so the layout only changes when the view mode or pages change.
/// </remarks>
public sealed class DocumentLayout
{
    public const double PointsToDips = 96.0 / 72.0;

    private readonly LayoutRect?[] _pages;
    private readonly List<Row> _rows;

    private DocumentLayout(LayoutRect?[] pages, List<Row> rows, double width, double height)
    {
        _pages = pages;
        _rows = rows;
        Width = width;
        Height = height;
    }

    /// <summary>Total size of the scrollable content at 100% zoom.</summary>
    public double Width { get; }

    public double Height { get; }

    public int PageCount => _pages.Length;

    public int RowCount => _rows.Count;

    /// <summary>Gap between pages and around the edge, in DIPs at 100%.</summary>
    public double Gap { get; private init; }

    /// <summary>The rectangle of a page, or null if the page is not shown in this mode (single page view).</summary>
    public LayoutRect? this[int pageIndex] => _pages[pageIndex];

    /// <summary>The widest and tallest row, used for "fit page".</summary>
    public (double Width, double Height) LargestRow { get; private init; }

    /// <param name="pageSizes">Page sizes in PDF points (as displayed, i.e. after rotation).</param>
    /// <param name="currentPage">Only used by <see cref="ViewMode.SinglePage"/>: which page to show.</param>
    public static DocumentLayout Compute(
        IReadOnlyList<(double Width, double Height)> pageSizes,
        ViewMode mode,
        int currentPage = 0,
        double gap = 12)
    {
        var rows = new List<Row>();
        foreach (int[] group in GroupPages(pageSizes.Count, mode, currentPage))
        {
            double w = group.Sum(i => pageSizes[i].Width * PointsToDips) + (gap * (group.Length - 1));
            double h = group.Max(i => pageSizes[i].Height * PointsToDips);
            rows.Add(new Row(group, 0, w, h));
        }

        double contentWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Width) + (2 * gap);
        var pages = new LayoutRect?[pageSizes.Count];
        double y = gap;
        double maxRowW = 0, maxRowH = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r] with { Y = y };
            rows[r] = row;
            maxRowW = Math.Max(maxRowW, row.Width);
            maxRowH = Math.Max(maxRowH, row.Height);

            // Centre the row; pages in a spread sit side by side with one gap between them.
            double x = (contentWidth - row.Width) / 2;
            foreach (int i in row.Pages)
            {
                double pw = pageSizes[i].Width * PointsToDips, ph = pageSizes[i].Height * PointsToDips;
                pages[i] = new LayoutRect(x, y + ((row.Height - ph) / 2), pw, ph);
                x += pw + gap;
            }

            y += row.Height + gap;
        }

        return new DocumentLayout(pages, rows, contentWidth, rows.Count == 0 ? 0 : y)
        {
            Gap = gap,
            LargestRow = (maxRowW, maxRowH),
        };
    }

    /// <summary>Pages that overlap the vertical band [top, bottom], in order. Binary search: O(log n).</summary>
    public IEnumerable<int> PagesInView(double top, double bottom)
    {
        int first = FirstRowEndingAfter(top);
        for (int r = first; r < _rows.Count && _rows[r].Y <= bottom; r++)
        {
            foreach (int page in _rows[r].Pages)
            {
                yield return page;
            }
        }
    }

    /// <summary>The page at a vertical position, i.e. the "current page" while scrolling.</summary>
    public int PageAt(double y)
    {
        if (_rows.Count == 0)
        {
            return 0;
        }

        int r = Math.Min(FirstRowEndingAfter(y), _rows.Count - 1);
        return _rows[r].Pages[0];
    }

    /// <summary>The first page of the row before/after the row containing <paramref name="pageIndex"/>.</summary>
    public int StepPage(int pageIndex, int rows)
    {
        int r = RowOf(pageIndex);
        if (r < 0)
        {
            return Math.Clamp(pageIndex + rows, 0, _pages.Length - 1);
        }

        return _rows[Math.Clamp(r + rows, 0, _rows.Count - 1)].Pages[0];
    }

    /// <summary>All the pages in the row that contains <paramref name="pageIndex"/> (one page, or a spread).</summary>
    public IReadOnlyList<int> RowPages(int pageIndex)
    {
        int r = RowOf(pageIndex);
        return r < 0 ? [pageIndex] : _rows[r].Pages;
    }

    /// <summary>Page groups for each mode. Public so the print and grid views can reuse the pairing rules.</summary>
    public static IEnumerable<int[]> GroupPages(int pageCount, ViewMode mode, int currentPage = 0)
    {
        if (pageCount <= 0)
        {
            yield break;
        }

        switch (mode)
        {
            case ViewMode.SinglePage:
                yield return [Math.Clamp(currentPage, 0, pageCount - 1)];
                break;

            case ViewMode.Continuous:
                for (int i = 0; i < pageCount; i++)
                {
                    yield return [i];
                }

                break;

            case ViewMode.TwoPage:
                for (int i = 0; i < pageCount; i += 2)
                {
                    yield return i + 1 < pageCount ? [i, i + 1] : [i];
                }

                break;

            case ViewMode.Book:
                yield return [0];
                for (int i = 1; i < pageCount; i += 2)
                {
                    yield return i + 1 < pageCount ? [i, i + 1] : [i];
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private int RowOf(int pageIndex)
    {
        if (_pages.Length == 0 || pageIndex < 0 || pageIndex >= _pages.Length || _pages[pageIndex] is not { } rect)
        {
            return -1;
        }

        int r = FirstRowEndingAfter(rect.Y);
        return r < _rows.Count ? r : -1;
    }

    private int FirstRowEndingAfter(double y)
    {
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Y + _rows[mid].Height < y)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private readonly record struct Row(int[] Pages, double Y, double Width, double Height);
}
