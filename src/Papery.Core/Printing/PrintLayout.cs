using Papery.Core.Pages;

namespace Papery.Core.Printing;

public enum PrintScaling
{
    /// <summary>Shrink or enlarge each page to fill its space on the paper.</summary>
    Fit,

    /// <summary>Real size; only shrink pages that are too big for the paper.</summary>
    ShrinkOversized,

    /// <summary>Exactly 100%, even if that crops the page.</summary>
    ActualSize,

    /// <summary>A percentage chosen by the user.</summary>
    Custom,
}

/// <summary>One page placed on one side of a sheet. Coordinates are fractions (0-1) of the printable area.</summary>
/// <param name="PageIndex">Zero-based page, or -1 for a blank (booklet padding).</param>
/// <param name="QuarterTurns">Clockwise quarter turns applied when printing (to match the cell's orientation).</param>
public sealed record PlacedPage(int PageIndex, double X, double Y, double Width, double Height, int QuarterTurns);

/// <summary>Everything printed on one side of one piece of paper.</summary>
public sealed record PrintSheet(IReadOnlyList<PlacedPage> Pages);

/// <summary>
/// Print imposition maths: page ranges, several pages per sheet, booklets and scaling.
/// No printer code here, so it is all unit-tested.
/// </summary>
public static class PrintLayout
{
    public static readonly IReadOnlyList<int> PagesPerSheetChoices = [1, 2, 4, 6, 9, 16];

    /// <summary>Parses the pages to print, e.g. "1-3, 5, 8-". See <see cref="PageRanges.TryParse"/>.</summary>
    public static bool TryParseRange(string? text, int pageCount, out IReadOnlyList<int> pages, out string? error) =>
        PageRanges.TryParse(text, pageCount, out pages, out error, "Enter the pages to print, for example 1-3, 5.");

    /// <summary>Columns × rows for N pages per sheet. On landscape paper the grid is wider than tall.</summary>
    public static (int Columns, int Rows) Grid(int pagesPerSheet, bool landscapePaper)
    {
        (int a, int b) = pagesPerSheet switch
        {
            1 => (1, 1),
            2 => (2, 1),
            4 => (2, 2),
            6 => (3, 2),
            9 => (3, 3),
            16 => (4, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(pagesPerSheet), "Use 1, 2, 4, 6, 9 or 16."),
        };

        // a >= b. Wide grids suit landscape paper; on portrait paper flip them.
        return landscapePaper ? (a, b) : (b, a);
    }

    /// <summary>Paper orientation that best fits N-up cells of the given page shape.</summary>
    public static bool PreferLandscapePaper(int pagesPerSheet, bool pagesAreLandscape) =>
        pagesPerSheet is 2 or 6 ? !pagesAreLandscape : pagesAreLandscape;

    /// <summary>
    /// Arranges pages onto sheet sides. For a booklet, pages are reordered so that the printed
    /// stack, folded in half, reads in order: print double-sided, flipping on the short edge.
    /// </summary>
    public static IReadOnlyList<PrintSheet> Impose(IReadOnlyList<int> pages, int pagesPerSheet, bool booklet, bool landscapePaper)
    {
        if (booklet)
        {
            return ImposeBooklet(pages);
        }

        var (cols, rows) = Grid(pagesPerSheet, landscapePaper);
        var sheets = new List<PrintSheet>();
        for (int start = 0; start < pages.Count; start += pagesPerSheet)
        {
            var placed = new List<PlacedPage>();
            for (int k = 0; k < pagesPerSheet && start + k < pages.Count; k++)
            {
                // Left-to-right, then top-to-bottom (the order people read).
                int col = k % cols, row = k / cols;
                placed.Add(new PlacedPage(pages[start + k], col / (double)cols, row / (double)rows, 1.0 / cols, 1.0 / rows, 0));
            }

            sheets.Add(new PrintSheet(placed));
        }

        return sheets;
    }

    /// <summary>
    /// Booklet order for n pages padded to a multiple of 4. Sheet side i (front/back alternate) holds two
    /// pages side by side on landscape paper: front = (last, first), back = (second, second-last), and so on.
    /// </summary>
    public static IReadOnlyList<PrintSheet> ImposeBooklet(IReadOnlyList<int> pages)
    {
        int n = (pages.Count + 3) / 4 * 4;
        int PageAt(int i) => i < pages.Count ? pages[i] : -1;

        var sides = new List<PrintSheet>();
        for (int s = 0; s < n / 4; s++)
        {
            int frontLeft = n - 1 - (2 * s), frontRight = 2 * s;
            int backLeft = (2 * s) + 1, backRight = n - 2 - (2 * s);
            sides.Add(new PrintSheet([new(PageAt(frontLeft), 0, 0, 0.5, 1, 0), new(PageAt(frontRight), 0.5, 0, 0.5, 1, 0)]));
            sides.Add(new PrintSheet([new(PageAt(backLeft), 0, 0, 0.5, 1, 0), new(PageAt(backRight), 0.5, 0, 0.5, 1, 0)]));
        }

        return sides;
    }

    /// <summary>
    /// Where a page goes inside its cell: rotated to match the cell's shape if that fits better,
    /// scaled per <paramref name="scaling"/>, and centred. All sizes in the same units (e.g. printer pixels).
    /// </summary>
    /// <returns>The page rectangle (relative to the cell) and quarter turns.</returns>
    public static (double X, double Y, double Width, double Height, int QuarterTurns) FitPage(
        double pageWidth, double pageHeight, double cellWidth, double cellHeight,
        PrintScaling scaling, double customPercent = 100, bool autoRotate = true)
    {
        int turns = 0;
        if (autoRotate && (pageWidth > pageHeight) != (cellWidth > cellHeight) && Math.Abs(pageWidth - pageHeight) > 1)
        {
            (pageWidth, pageHeight) = (pageHeight, pageWidth);
            turns = 1;
        }

        double fit = Math.Min(cellWidth / pageWidth, cellHeight / pageHeight);
        double scale = scaling switch
        {
            PrintScaling.Fit => fit,
            PrintScaling.ShrinkOversized => Math.Min(1.0, fit),
            PrintScaling.ActualSize => 1.0,
            PrintScaling.Custom => Math.Clamp(customPercent, 10, 400) / 100.0,
            _ => fit,
        };

        double w = pageWidth * scale, h = pageHeight * scale;
        return ((cellWidth - w) / 2, (cellHeight - h) / 2, w, h, turns);
    }
}
