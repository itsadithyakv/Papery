using Papery.Core.Printing;

namespace Papery.Core.Tests;

public sealed class PrintLayoutTests
{
    [Theory]
    [InlineData("1-3, 5", new[] { 0, 1, 2, 4 })]
    [InlineData(" 2 - 4 ", new[] { 1, 2, 3 })]
    [InlineData("8-", new[] { 7, 8, 9 })]
    [InlineData("-2", new[] { 0, 1 })]
    [InlineData("3;1", new[] { 2, 0 })]
    [InlineData("1,1", new[] { 0, 0 })]
    [InlineData("4–5", new[] { 3, 4 })] // en dash, as typed by Word users
    public void Page_ranges_are_parsed(string text, int[] expected)
    {
        Assert.True(PrintLayout.TryParseRange(text, 10, out var pages, out string? error), error);
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("5-2")]
    [InlineData("abc")]
    [InlineData("1-x")]
    [InlineData("-")]
    public void Bad_ranges_give_a_friendly_error(string text)
    {
        Assert.False(PrintLayout.TryParseRange(text, 10, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Four_up_fills_left_to_right_then_down()
    {
        var sheets = PrintLayout.Impose([0, 1, 2, 3, 4], 4, booklet: false, landscapePaper: false);

        Assert.Equal(2, sheets.Count);
        var first = sheets[0].Pages;
        Assert.Equal(4, first.Count);
        Assert.Equal((0.0, 0.0), (first[0].X, first[0].Y));
        Assert.Equal((0.5, 0.0), (first[1].X, first[1].Y));
        Assert.Equal((0.0, 0.5), (first[2].X, first[2].Y));
        Assert.Single(sheets[1].Pages);
    }

    [Fact]
    public void Booklet_of_8_pages_has_the_classic_order()
    {
        var sides = PrintLayout.ImposeBooklet([0, 1, 2, 3, 4, 5, 6, 7]);

        // Pages are 0-based: printed page numbers 8|1, 2|7, 6|3, 4|5.
        int[][] expected = [[7, 0], [1, 6], [5, 2], [3, 4]];
        Assert.Equal(expected.Length, sides.Count);
        for (int i = 0; i < sides.Count; i++)
        {
            Assert.Equal(expected[i], sides[i].Pages.Select(p => p.PageIndex));
        }
    }

    [Fact]
    public void Booklet_pads_with_blank_pages_to_a_multiple_of_four()
    {
        var sides = PrintLayout.ImposeBooklet([0, 1, 2, 3, 4]);

        Assert.Equal(4, sides.Count); // 8 page slots on 2 sheets, double-sided
        int blanks = sides.SelectMany(s => s.Pages).Count(p => p.PageIndex == -1);
        Assert.Equal(3, blanks);
        var all = sides.SelectMany(s => s.Pages).Where(p => p.PageIndex >= 0).Select(p => p.PageIndex).Order();
        Assert.Equal([0, 1, 2, 3, 4], all);
    }

    [Fact]
    public void Fit_rotates_landscape_pages_onto_portrait_cells()
    {
        var (_, _, w, h, turns) = PrintLayout.FitPage(842, 595, 600, 800, PrintScaling.Fit);

        Assert.Equal(1, turns);
        Assert.True(h > w);
        Assert.True(w <= 600.001 && h <= 800.001);
    }

    [Fact]
    public void Shrink_oversized_never_enlarges()
    {
        var (_, _, w, h, _) = PrintLayout.FitPage(100, 100, 1000, 1000, PrintScaling.ShrinkOversized);

        Assert.Equal((100.0, 100.0), (w, h));
    }

    [Fact]
    public void Content_is_centred_in_its_cell()
    {
        var (x, y, w, h, _) = PrintLayout.FitPage(500, 500, 1000, 600, PrintScaling.ActualSize, autoRotate: false);

        Assert.Equal(250, x);
        Assert.Equal(50, y);
        Assert.Equal((500.0, 500.0), (w, h));
    }

    [Theory]
    [InlineData(2, false, 1, 2)]
    [InlineData(2, true, 2, 1)]
    [InlineData(6, false, 2, 3)]
    [InlineData(9, true, 3, 3)]
    public void Grid_shapes(int perSheet, bool landscape, int cols, int rows) =>
        Assert.Equal((cols, rows), PrintLayout.Grid(perSheet, landscape));
}
