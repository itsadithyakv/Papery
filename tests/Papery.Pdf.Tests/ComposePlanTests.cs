using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

public sealed class ComposePlanTests
{
    private static PageRef P(int page, int source = 0) => new(source, page);

    [Fact]
    public void Unchanged_document_needs_no_steps()
    {
        var plan = ComposePlan.Create([P(0), P(1), P(2)], inPlaceSource: 0, inPlacePageCount: 3);

        Assert.Empty(plan.Imports);
        Assert.Empty(plan.Deletions);
        Assert.Null(plan.Order);
    }

    [Fact]
    public void Reorder_in_place_only_moves()
    {
        var plan = ComposePlan.Create([P(2), P(0), P(1)], 0, 3);

        Assert.Empty(plan.Imports);
        Assert.Empty(plan.Deletions);
        Assert.Equal([2, 0, 1], plan.Order!);
    }

    [Fact]
    public void Removed_pages_are_deleted_highest_first_and_order_uses_new_indices()
    {
        // Keep pages 4 and 1 of 0..4, in that order.
        var plan = ComposePlan.Create([P(4), P(1)], 0, 5);

        Assert.Equal([3, 2, 0], plan.Deletions);
        // After deleting 0, 2, 3: page 1 is at 0, page 4 is at 1.
        Assert.Equal([1, 0], plan.Order!);
    }

    [Fact]
    public void Duplicates_and_other_files_are_imported_once_per_source()
    {
        PageRef[] pages = [P(0), P(5, source: 1), P(0), P(6, source: 1), P(1)];
        var plan = ComposePlan.Create(pages, 0, 2);

        Assert.Equal(2, plan.Imports.Count);
        Assert.Equal(1, plan.Imports[0].Source);
        Assert.Equal([5, 6], plan.Imports[0].Pages);
        Assert.Equal(0, plan.Imports[1].Source); // the duplicate of page 0
        Assert.Equal([0], plan.Imports[1].Pages);
        // Pool: 0,1 (originals), 2,3 (file 1), 4 (copy of 0).
        Assert.Equal([0, 2, 4, 3, 1], plan.Order!);
    }

    [Fact]
    public void Blank_pages_are_added_after_imports()
    {
        var plan = ComposePlan.Create([PageRef.Blank(new PdfPageSize(100, 200)), P(0)], 0, 1);

        Assert.Single(plan.Blanks);
        Assert.Equal([1, 0], plan.Order!);
    }

    [Fact]
    public void New_document_imports_everything_in_source_runs()
    {
        PageRef[] pages = [P(0, 1), P(1, 1), P(0, 2), P(3, 1)];
        var plan = ComposePlan.Create(pages, inPlaceSource: null, inPlacePageCount: 0);

        Assert.Equal([0, 1, 3], plan.Imports[0].Pages);
        Assert.Equal([0], plan.Imports[1].Pages);
        Assert.Empty(plan.Deletions);
        Assert.Equal([0, 1, 3, 2], plan.Order!);
    }

    [Fact]
    public void Rotation_wraps_both_ways()
    {
        Assert.Equal(3, P(0).Rotated(-1).QuarterTurns);
        Assert.Equal(0, P(0).Rotated(1).Rotated(1).Rotated(1).Rotated(1).QuarterTurns);
    }
}
