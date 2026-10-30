using Papery.Core.Text;

namespace Papery.Core.Tests;

public sealed class WordDiffTests
{
    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void The_same_words_have_no_differences()
    {
        var (removed, added) = WordDiff.Compare(Words("a b c"), Words("a b c"));

        Assert.Empty(removed);
        Assert.Empty(added);
    }

    [Fact]
    public void A_changed_word_shows_on_both_sides()
    {
        var (removed, added) = WordDiff.Compare(Words("the quick fox"), Words("the slow fox"));

        Assert.Equal([1], removed);
        Assert.Equal([1], added);
    }

    [Fact]
    public void Added_and_removed_words_keep_their_places()
    {
        var (removed, added) = WordDiff.Compare(Words("one two three four"), Words("zero one three four five"));

        Assert.Equal([1], removed); // two
        Assert.Equal([0, 4], added); // zero, five
    }

    [Theory]
    [InlineData("", "a b", 0, 2)]
    [InlineData("a b", "", 2, 0)]
    [InlineData("", "", 0, 0)]
    public void Empty_sides_are_fine(string left, string right, int removedCount, int addedCount)
    {
        var (removed, added) = WordDiff.Compare(Words(left), Words(right));

        Assert.Equal(removedCount, removed.Count);
        Assert.Equal(addedCount, added.Count);
    }

    [Fact]
    public void Enormous_pages_finish_quickly_and_still_report_changes()
    {
        string[] left = Enumerable.Range(0, 30_000).Select(i => $"a{i}").ToArray();
        string[] right = Enumerable.Range(0, 30_000).Select(i => $"b{i}").ToArray();

        var (removed, added) = WordDiff.Compare(left, right);

        Assert.Equal(30_000, removed.Count);
        Assert.Equal(30_000, added.Count);
    }
}
