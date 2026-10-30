using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

/// <summary>
/// Crop, paper size, margins and straightening: the real PDF output is checked against the
/// preview maths (<see cref="PageGeometry.Layout"/>), by finding where a known word ends up.
/// </summary>
public sealed class PageGeometryTests : IDisposable
{
    private const double Tolerance = 0.01; // 1% of the page

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private async Task<string> ApplyAsync(string path, params PageRef[] pages)
    {
        string output = Path.Combine(_files.Folder, $"out-{Guid.NewGuid():N}.pdf");
        await Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], pages, EditInPlaceOf: 0), output);
        return output;
    }

    /// <summary>Where the word sits on the page (fractions), from PDFium's own text layout.</summary>
    private static async Task<NormalizedRect> Find(string path, string word, int page = 0)
    {
        await using var doc = await Engine.OpenAsync(path);
        var hits = await doc.FindOnPageAsync(page, word);
        return Assert.Single(hits).Areas[0];
    }

    private static async Task<PdfPageSize> SizeOf(string path, int page = 0)
    {
        await using var doc = await Engine.OpenAsync(path);
        return doc.PageSizes[page];
    }

    /// <summary>Where a point of the original page lands according to the preview maths.</summary>
    private static (double X, double Y) Expected(GeometryLayout layout, double x, double y) =>
        (layout.Content.X + ((x - layout.Source.X) / layout.Source.Width * layout.Content.Width),
         layout.Content.Y + ((y - layout.Source.Y) / layout.Source.Height * layout.Content.Height));

    private static async Task AssertMatchesPreview(string original, string result, PageGeometry geometry, string word)
    {
        var before = await Find(original, word);
        var after = await Find(result, word);
        var layout = geometry.Layout(await SizeOf(original));
        var (x, y) = Expected(layout, before.X, before.Y);

        Assert.Equal(layout.Paper.Width, (await SizeOf(result)).Width, 1f);
        Assert.Equal(layout.Paper.Height, (await SizeOf(result)).Height, 1f);
        Assert.InRange(after.X, x - Tolerance, x + Tolerance);
        Assert.InRange(after.Y, y - Tolerance, y + Tolerance);
    }

    [Fact]
    public async Task Crop_keeps_the_chosen_part_at_its_real_size()
    {
        string path = _files.Create(1, "Hello");
        var geometry = new PageGeometry(Crop: new NormalizedRect(0.05, 0.05, 0.5, 0.4));

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry));

        await AssertMatchesPreview(path, result, geometry, "Hello");
    }

    [Fact]
    public async Task New_paper_size_scales_content_to_fit()
    {
        string path = _files.Create(1, "Hello");
        var geometry = new PageGeometry(Paper: PaperSizes.A3);

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry));

        await AssertMatchesPreview(path, result, geometry, "Hello");
    }

    [Fact]
    public async Task Keep_size_centres_the_page_on_bigger_paper()
    {
        string path = _files.Create(1, "Hello");
        var geometry = new PageGeometry(Paper: PaperSizes.A3, FitContent: false, Margin: 20);

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry));

        await AssertMatchesPreview(path, result, geometry, "Hello");
    }

    [Fact]
    public async Task Margins_alone_make_the_page_bigger()
    {
        string path = _files.Create(1, "Hello");
        var geometry = new PageGeometry(Margin: 36);

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry));

        Assert.Equal(595.28f + 72, (await SizeOf(result)).Width, 1f);
        await AssertMatchesPreview(path, result, geometry, "Hello");
    }

    [Fact]
    public async Task Crop_then_paper_and_margin_together()
    {
        string path = _files.Create(1, "Hello");
        var geometry = new PageGeometry(Crop: new NormalizedRect(0.1, 0.05, 0.6, 0.5), Paper: PaperSizes.Letter, Margin: 30);

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry));

        await AssertMatchesPreview(path, result, geometry, "Hello");
    }

    [Fact]
    public async Task Crop_on_a_turned_page_uses_the_page_as_the_user_sees_it()
    {
        string path = _files.Create(1, "Hello");

        // Turned a quarter, the A4 page is 842 wide; keep its left half.
        string result = await ApplyAsync(path, new PageRef(0, 0, QuarterTurns: 1, Geometry: new PageGeometry(Crop: new NormalizedRect(0, 0, 0.5, 1))));

        var size = await SizeOf(result);
        Assert.Equal(841.89f / 2, size.Width, 1f);
        Assert.Equal(595.28f, size.Height, 1f);
    }

    [Fact]
    public async Task Paper_size_on_a_turned_page_keeps_the_orientation_asked_for()
    {
        string path = _files.Create(1, "Hello");
        var landscapeA3 = new PdfPageSize(PaperSizes.A3.Height, PaperSizes.A3.Width);

        string result = await ApplyAsync(path, new PageRef(0, 0, QuarterTurns: 1, Geometry: new PageGeometry(Paper: landscapeA3)));

        var size = await SizeOf(result);
        Assert.Equal(landscapeA3.Width, size.Width, 1f);
        Assert.Equal(landscapeA3.Height, size.Height, 1f);
    }

    [Fact]
    public async Task Links_move_with_the_content()
    {
        string path = _files.CreateNavigable();
        var geometry = new PageGeometry(Paper: PaperSizes.A3, Margin: 50);
        PdfLink before;
        await using (var doc = await Engine.OpenAsync(path))
        {
            before = (await doc.GetLinksAsync(0)).First(l => l.TargetPage == 2);
        }

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: geometry), new PageRef(0, 1), new PageRef(0, 2));

        await using var after = await Engine.OpenAsync(result);
        var link = (await after.GetLinksAsync(0)).First(l => l.TargetPage == 2);
        var (x, y) = Expected(geometry.Layout(new PdfPageSize(595.28f, 841.89f)), before.Area.X, before.Area.Y);
        Assert.InRange(link.Area.X, x - Tolerance, x + Tolerance);
        Assert.InRange(link.Area.Y, y - Tolerance, y + Tolerance);
    }

    [Fact]
    public async Task Straightening_turns_the_content_clockwise_about_the_middle()
    {
        string path = _files.Create(1, "Hello");
        var before = await Find(path, "Hello");

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: new PageGeometry(StraightenDegrees: 10)));

        // A point up and to the left of the middle moves right and up when turned clockwise.
        var after = await Find(result, "Hello");
        Assert.True(after.X > before.X + 0.02, $"x {before.X:0.000} -> {after.X:0.000}");
        Assert.True(after.Y < before.Y - 0.005, $"y {before.Y:0.000} -> {after.Y:0.000}");
        Assert.Equal(595.28f, (await SizeOf(result)).Width, 1f);
    }

    [Fact]
    public async Task Splitting_a_double_page_is_two_crops_of_the_same_page()
    {
        string path = _files.Create(1, "Hello");
        var left = new PageGeometry(Crop: new NormalizedRect(0, 0, 0.5, 1));
        var right = new PageGeometry(Crop: new NormalizedRect(0.5, 0, 0.5, 1));

        string result = await ApplyAsync(path, new PageRef(0, 0, Geometry: left), new PageRef(0, 0, Geometry: right));

        await using var doc = await Engine.OpenAsync(result);
        Assert.Equal(2, doc.PageCount);
        Assert.All(doc.PageSizes, s => Assert.Equal(595.28f / 2, s.Width, 1f));
        Assert.InRange(Assert.Single(await doc.FindOnPageAsync(0, "Hello")).Areas[0].X, 0, 1);

        // Cropping hides the rest of the page; it doesn't delete it (that's Redact's job). The word is
        // still in the right half's file, but off to the left of what is shown.
        var hidden = Assert.Single(await doc.FindOnPageAsync(1, "Hello")).Areas[0];
        Assert.True(hidden.Right < 0, $"expected the word left of the visible area, at {hidden.X:0.00}");
    }

    [Fact]
    public async Task Two_up_puts_two_pages_on_each_landscape_sheet()
    {
        string path = _files.Create(5, "Sheet");
        string output = Path.Combine(_files.Folder, "2up.pdf");
        var sheet = new PdfPageSize(PaperSizes.A4.Height, PaperSizes.A4.Width);

        var result = await Engine.NUpAsync(new PdfInputFile(path), output, sheet, across: 2, down: 1);

        Assert.Equal(3, result.PageCount);
        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(sheet.Width, doc.PageSizes[0].Width, 1f);
        string text = await doc.GetPageTextAsync(0);
        Assert.Contains("Sheet 1", text, StringComparison.Ordinal);
        Assert.Contains("Sheet 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_layout_centres_content_inside_the_margins()
    {
        var layout = new PageGeometry(Paper: new PdfPageSize(200, 400), Margin: 20).Layout(new PdfPageSize(100, 100));

        // 160 wide inside the margins, so the square content is 160 x 160, centred.
        Assert.Equal(20 / 200.0, layout.Content.X, 3);
        Assert.Equal(160 / 200.0, layout.Content.Width, 3);
        Assert.Equal(120 / 400.0, layout.Content.Y, 3);
    }

    [Fact]
    public void Paper_turns_to_match_the_page()
    {
        var landscape = new PdfPageSize(800, 500);
        var oriented = PaperSizes.Oriented(PaperSizes.A4, landscape);
        Assert.True(oriented.Width > oriented.Height);
    }
}
