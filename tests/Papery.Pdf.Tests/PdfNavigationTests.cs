namespace Papery.Pdf.Tests;

public sealed class PdfNavigationTests : IDisposable
{
    private readonly TestPdf _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task Search_finds_matches_with_highlight_areas_and_context()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());

        var hits = await doc.FindOnPageAsync(1, "lighthouse");

        var hit = Assert.Single(hits);
        Assert.Equal(1, hit.PageIndex);
        Assert.Contains("Page 2 lighthouse", hit.Context, StringComparison.Ordinal);
        var area = Assert.Single(hit.Areas);
        // "Page 2 lighthouse" starts 72pt in on a 595pt page; the match is the word after "Page 2 ".
        Assert.InRange(area.X, 0.15, 0.40);
        Assert.InRange(area.Y, 0.05, 0.15);
        Assert.True(area.Width > 0 && area.Height > 0);
    }

    [Fact]
    public async Task Search_options_respect_case()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());

        Assert.Single(await doc.FindOnPageAsync(0, "LIGHTHOUSE"));
        Assert.Empty(await doc.FindOnPageAsync(0, "LIGHTHOUSE", PdfSearchOptions.MatchCase));
        Assert.Empty(await doc.FindOnPageAsync(0, "   "));
    }

    [Fact]
    public async Task Text_can_be_selected_by_position_and_by_word()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());
        var hit = (await doc.FindOnPageAsync(0, "lighthouse")).Single();
        var box = hit.Areas[0];

        int index = await doc.GetCharIndexAtAsync(0, box.X + (box.Width / 2), box.Y + (box.Height / 2));
        var word = await doc.GetWordAtAsync(0, index);
        var range = await doc.GetSelectionAsync(0, hit.CharIndex + hit.Length - 1, 0); // backwards drag

        Assert.InRange(index, hit.CharIndex, hit.CharIndex + hit.Length - 1);
        Assert.Equal("lighthouse", word.Text);
        Assert.Equal("Page 1 lighthouse", range.Text);
        Assert.NotEmpty(range.Areas);
        Assert.Equal(-1, await doc.GetCharIndexAtAsync(0, 0.95, 0.95)); // empty corner
    }

    [Fact]
    public async Task Outline_is_read_as_a_tree_with_page_numbers()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());

        var outline = await doc.GetOutlineAsync();

        Assert.Equal(["Chapter One", "Chapter Two"], outline.Select(o => o.Title));
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Equal(2, outline[1].PageIndex);
        var section = Assert.Single(outline[0].Children);
        Assert.Equal(("Section 1.1", (int?)1), (section.Title, section.PageIndex));
    }

    [Fact]
    public async Task Links_include_internal_jumps_web_links_and_plain_text_urls()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());

        var links = await doc.GetLinksAsync(0);

        Assert.Contains(links, l => l.TargetPage == 2);
        Assert.Contains(links, l => l.Uri == "https://example.com/annot");
        Assert.Contains(links, l => l.Uri != null && l.Uri.StartsWith("https://example.org/plain", StringComparison.Ordinal));
        Assert.All(links, l => Assert.InRange(l.Area.X, 0, 1));
    }

    [Fact]
    public async Task Metadata_is_read()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.CreateNavigable());

        var meta = await doc.GetMetadataAsync();

        Assert.Equal("Navigation test", meta.Title);
        Assert.Equal("Papery Tests", meta.Author);
        Assert.NotNull(meta.Created);
    }

    [Fact]
    public async Task A_tile_matches_the_same_area_of_a_full_render()
    {
        await using var doc = await PdfEngine.Shared.OpenAsync(_files.Create(pages: 1, text: "Tile"));

        var full = await doc.RenderPageAsync(0, 600, 848);
        var tile = await doc.RenderRegionAsync(0, 600, 848, new PixelRect(50, 60, 200, 100));

        int differences = 0;
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                int ti = (y * tile.Stride) + (x * 4);
                int fi = ((y + 60) * full.Stride) + ((x + 50) * 4);
                if (Math.Abs(tile.Pixels[ti] - full.Pixels[fi]) > 8)
                {
                    differences++;
                }
            }
        }

        Assert.True(differences < 50, $"{differences} pixels differ between tile and full render.");
    }

    [Theory]
    [InlineData("D:20240131093000+01'00'", 2024, 1, 31, 9, 30, 60)]
    [InlineData("D:20231225", 2023, 12, 25, 0, 0, 0)]
    [InlineData("D:2020", 2020, 1, 1, 0, 0, 0)]
    [InlineData("20221005120000Z", 2022, 10, 5, 12, 0, 0)]
    [InlineData("D:19991231235959-05'30'", 1999, 12, 31, 23, 59, -330)]
    public void Pdf_dates_are_parsed(string text, int y, int mo, int d, int h, int mi, int offsetMinutes)
    {
        var date = PdfDocument.ParsePdfDate(text);

        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, date?.Second ?? 0, TimeSpan.FromMinutes(offsetMinutes)), date);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("D:20241345")]
    public void Bad_pdf_dates_give_null(string? text) => Assert.Null(PdfDocument.ParsePdfDate(text));
}
