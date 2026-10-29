using Papery.Core.Pages;

namespace Papery.Core.Tests;

public sealed class PageRangesTests
{
    [Fact]
    public void Groups_keep_each_part_separate()
    {
        Assert.True(PageRanges.TryParseGroups("1-3, 4-5 ; 9", 10, out var groups, out _));

        Assert.Equal(3, groups.Count);
        Assert.Equal([0, 1, 2], groups[0]);
        Assert.Equal([8], groups[2]);
    }

    [Fact]
    public void Groups_report_the_bad_part()
    {
        Assert.False(PageRanges.TryParseGroups("1-3, 12", 10, out _, out string? error));
        Assert.Equal("This document has pages 1 to 10.", error);
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 4 }, "1-3, 5")]
    [InlineData(new[] { 6 }, "7")]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 3, 2 }, "4, 3")]
    public void Format_is_compact(int[] pages, string expected) => Assert.Equal(expected, PageRanges.Format(pages));

    [Fact]
    public void Empty_text_has_a_neutral_message()
    {
        Assert.False(PageRanges.TryParse(" ", 4, out _, out string? error));
        Assert.Equal("Enter pages, for example 1-3, 5.", error);
    }
}
