using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class SplitPlannerTests
{
    private static PdfOutlineItem B(string title, int? page, params PdfOutlineItem[] children) => new(title, page, children);

    [Fact]
    public void Every_n_pages_leaves_a_short_last_part()
    {
        var parts = SplitPlanner.EveryNPages(7, 3);

        Assert.Equal(3, parts.Count);
        Assert.Equal([0, 1, 2], parts[0].Pages);
        Assert.Equal([6], parts[2].Pages);
        Assert.Equal("part 01", parts[0].Name);
    }

    [Fact]
    public void Part_numbers_are_padded_so_files_sort_in_order()
    {
        var parts = SplitPlanner.EveryNPages(120, 1);

        Assert.Equal("part 001", parts[0].Name);
        Assert.Equal("part 120", parts[119].Name);
    }

    [Fact]
    public void Bookmarks_split_into_chapters_with_a_start_part()
    {
        var outline = new[] { B("Intro", 2), B("Methods", 5), B("Results", 9) };

        var parts = SplitPlanner.ByBookmarks(outline, 12);

        Assert.Equal(["01 Start", "02 Intro", "03 Methods", "04 Results"], parts.Select(p => p.Name));
        Assert.Equal([0, 1], parts[0].Pages);
        Assert.Equal([5, 6, 7, 8], parts[2].Pages);
        Assert.Equal([9, 10, 11], parts[3].Pages);
    }

    [Fact]
    public void A_single_title_bookmark_uses_its_children_as_chapters()
    {
        var outline = new[] { B("My Book", 0, B("One", 0), B("Two", 4)) };

        var parts = SplitPlanner.ByBookmarks(outline, 8);

        Assert.Equal(["01 One", "02 Two"], parts.Select(p => p.Name));
    }

    [Fact]
    public void Bookmarks_out_of_order_or_on_the_same_page_still_make_clean_parts()
    {
        var outline = new[] { B("B", 4), B("A", 0), B("A again", 0), B("Broken", 99) };

        var parts = SplitPlanner.ByBookmarks(outline, 6);

        Assert.Equal(["01 A", "02 B"], parts.Select(p => p.Name));
        Assert.Equal(6, parts.Sum(p => p.Pages.Count));
    }

    [Fact]
    public void File_names_are_made_safe()
    {
        string name = SplitPlanner.FileName("Report.pdf", new SplitPart("02 Part 1: What? / Why \"now\"", [0]));

        Assert.Equal("Report - 02 Part 1 - What_ - Why _now_.pdf", name);
    }

    [Fact]
    public void Remapped_outline_drops_bookmarks_to_missing_pages_but_keeps_surviving_children()
    {
        var outline = new[] { B("Gone", 0, B("Kept child", 3)), B("Also gone", 1) };

        var result = SplitPlanner.RemapOutline(outline, p => p >= 2 ? p - 2 : null);

        var only = Assert.Single(result);
        Assert.Null(only.PageIndex);
        Assert.Equal(1, only.Children[0].PageIndex);
    }
}
