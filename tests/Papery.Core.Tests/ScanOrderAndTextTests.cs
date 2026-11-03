using Papery.Core.Imaging;
using Papery.Core.Pages;
using Papery.Core.Text;

namespace Papery.Core.Tests;

public sealed class ScanOrderAndTextTests
{
    // ================================================================= double-sided scans

    [Fact]
    public void Two_files_shuffle_front_back_front_back()
    {
        Assert.Equal([(0, 0), (1, 2), (0, 1), (1, 1), (0, 2), (1, 0)], ScanOrder.Shuffle(3, 3, backsReversed: true));
        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)], ScanOrder.Shuffle(2, 2, backsReversed: false));
    }

    [Fact]
    public void A_file_with_more_pages_finishes_at_the_end()
    {
        Assert.Equal([(0, 0), (1, 0), (0, 1), (0, 2)], ScanOrder.Shuffle(3, 1, backsReversed: true));
        Assert.Equal([(1, 1), (1, 0)], ScanOrder.Shuffle(0, 2, backsReversed: true));
    }

    [Fact]
    public void One_file_of_fronts_then_backs_comes_back_in_reading_order()
    {
        // Paper 1/2, 3/4, 5/6 scanned as fronts 1 3 5 then, turned over, backs 6 4 2.
        Assert.Equal([0, 5, 1, 4, 2, 3], ScanOrder.Shuffle(6, backsReversed: true));
        Assert.Equal([0, 3, 1, 4, 2, 5], ScanOrder.Shuffle(6, backsReversed: false));
        Assert.Equal([0, 4, 1, 3, 2], ScanOrder.Shuffle(5, backsReversed: true)); // the last sheet had no back
        Assert.Empty(ScanOrder.Shuffle(0, backsReversed: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(40)]
    public void Shuffling_keeps_every_page_once(int count) =>
        Assert.Equal(Enumerable.Range(0, count), ScanOrder.Shuffle(count, backsReversed: true).Order());

    // ================================================================= text files

    [Fact]
    public void Files_decode_as_they_were_saved()
    {
        Assert.Equal("café", PlainText.Decode([0xEF, 0xBB, 0xBF, .. "café"u8]));
        Assert.Equal("café", PlainText.Decode("café"u8));
        Assert.Equal("café", PlainText.Decode([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("café")]));
        Assert.Equal("café", PlainText.Decode([(byte)'c', (byte)'a', (byte)'f', 0xE9])); // an old Western file, not UTF-8
    }

    [Fact]
    public void Tidying_unifies_line_ends_expands_tabs_and_drops_control_characters()
    {
        Assert.Equal("a\nb\nc\n", PlainText.Tidy("a\r\nb\rc\n"));
        Assert.Equal("ab  c   d", PlainText.Tidy("ab\tc\td"));
        Assert.Equal("bell\fnext", PlainText.Tidy("be\u0007ll\fnext"));
    }

    [Fact]
    public void Wrapping_breaks_at_spaces_and_inside_words_that_are_too_long()
    {
        Assert.Equal(["the quick", "brown fox"], PlainText.Wrap("the quick brown fox", 10));
        Assert.Equal(["abcdefghij", "klm"], PlainText.Wrap("abcdefghijklm", 10));
        Assert.Equal(["    indented", "", "after"], PlainText.Wrap("    indented\n\nafter", 20));
        Assert.Equal(["one", "\f", "two"], PlainText.Wrap("one\ftwo", 20));
        Assert.Equal(["one", "\f"], PlainText.Wrap("one\f", 20));
        Assert.All(PlainText.Wrap(new string('x', 95) + " " + new string('y', 30), 40), line => Assert.True(line.Length <= 40));
    }

    // ================================================================= blank pages

    private static byte[] Page(int width, int height, byte paper = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = paper;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static void Ink(byte[] pixels, int width, int x, int y, int w, int h, byte value = 0)
    {
        for (int j = y; j < y + h; j++)
        {
            for (int i = x; i < x + w; i++)
            {
                int at = ((j * width) + i) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
            }
        }
    }

    [Fact]
    public void An_empty_page_and_a_grey_scan_with_dust_look_blank()
    {
        Assert.True(PageImageAnalysis.LooksBlank(Page(400, 560), 400, 560, 1600));

        var scan = Page(400, 560, paper: 228);
        Ink(scan, 400, 120, 200, 1, 1);                // dust
        Ink(scan, 400, 0, 0, 400, 12, 40);             // the scanner's dark edge
        Ink(scan, 400, 60, 300, 200, 3, 190);          // show-through from the other side, faint
        Assert.True(PageImageAnalysis.LooksBlank(scan, 400, 560, 1600));
    }

    [Fact]
    public void A_page_with_a_line_of_text_is_not_blank()
    {
        var page = Page(400, 560);
        for (int letter = 0; letter < 30; letter++)
        {
            Ink(page, 400, 40 + (letter * 10), 100, 6, 9);
        }

        Assert.False(PageImageAnalysis.LooksBlank(page, 400, 560, 1600));
    }
}
