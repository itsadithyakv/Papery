using Papery.Core.Viewing;

namespace Papery.Core.Tests;

public sealed class DocumentLayoutTests
{
    private static readonly (double, double) A4 = (595, 842);
    private static readonly (double, double) A4Landscape = (842, 595);

    private static List<(double Width, double Height)> Pages(int count) => Enumerable.Repeat(A4, count).ToList();

    [Fact]
    public void Continuous_stacks_every_page_in_order()
    {
        var layout = DocumentLayout.Compute(Pages(5), ViewMode.Continuous);

        Assert.Equal(5, layout.RowCount);
        for (int i = 1; i < 5; i++)
        {
            Assert.True(layout[i]!.Value.Y > layout[i - 1]!.Value.Bottom);
        }

        Assert.True(layout.Height > 5 * 842 * DocumentLayout.PointsToDips);
    }

    [Fact]
    public void Two_page_mode_pairs_1_2_then_3_4()
    {
        var layout = DocumentLayout.Compute(Pages(5), ViewMode.TwoPage);

        Assert.Equal(3, layout.RowCount);
        Assert.Equal(layout[0]!.Value.Y, layout[1]!.Value.Y);
        Assert.True(layout[1]!.Value.X > layout[0]!.Value.X);
        Assert.Equal([4], layout.RowPages(4));
    }

    [Fact]
    public void Book_mode_shows_the_cover_alone()
    {
        var layout = DocumentLayout.Compute(Pages(6), ViewMode.Book);

        Assert.Equal([0], layout.RowPages(0));
        Assert.Equal([1, 2], layout.RowPages(1));
        Assert.Equal([3, 4], layout.RowPages(4));
        Assert.Equal([5], layout.RowPages(5));
    }

    [Fact]
    public void Single_page_mode_lays_out_only_the_current_page()
    {
        var layout = DocumentLayout.Compute(Pages(10), ViewMode.SinglePage, currentPage: 3);

        Assert.Equal(1, layout.RowCount);
        Assert.NotNull(layout[3]);
        Assert.Null(layout[0]);
        Assert.Null(layout[4]);
    }

    [Fact]
    public void Mixed_widths_are_centred()
    {
        var layout = DocumentLayout.Compute([A4, A4Landscape], ViewMode.Continuous);

        double centre0 = layout[0]!.Value.X + (layout[0]!.Value.Width / 2);
        double centre1 = layout[1]!.Value.X + (layout[1]!.Value.Width / 2);
        Assert.Equal(centre0, centre1, 3);
        Assert.Equal(layout.Width / 2, centre0, 3);
    }

    [Fact]
    public void PagesInView_finds_only_visible_pages_in_a_huge_document()
    {
        var layout = DocumentLayout.Compute(Pages(10_000), ViewMode.Continuous);
        var page5000 = layout[5000]!.Value;

        var visible = layout.PagesInView(page5000.Y + 10, page5000.Y + 20).ToList();

        Assert.Equal([5000], visible);
        Assert.Equal(5000, layout.PageAt(page5000.Y + 100));
    }

    [Fact]
    public void StepPage_moves_by_rows_and_stops_at_the_ends()
    {
        var layout = DocumentLayout.Compute(Pages(6), ViewMode.TwoPage);

        Assert.Equal(2, layout.StepPage(0, 1));
        Assert.Equal(2, layout.StepPage(1, 1)); // from the right-hand page of the first spread
        Assert.Equal(4, layout.StepPage(4, 1)); // already on the last row
        Assert.Equal(0, layout.StepPage(2, -5));
    }

    [Fact]
    public void Empty_document_has_an_empty_layout()
    {
        var layout = DocumentLayout.Compute([], ViewMode.Continuous);

        Assert.Equal(0, layout.RowCount);
        Assert.Equal(0, layout.Height);
        Assert.Empty(layout.PagesInView(0, 1000));
        Assert.Equal(0, layout.PageAt(50));
    }
}
