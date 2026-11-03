using PdfSharp.Drawing;
using Papery.Pdf.Editing;

namespace Papery.Pdf.Tests;

/// <summary>Text to PDF, bookmarks from headings, clickable web addresses and fast web view.</summary>
public sealed class PdfQuickToolsTests : IDisposable
{
    private static readonly PdfPageSize A4 = new(595.28f, 841.89f);
    private readonly TestPdf _files = new();

    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string NewPath => Path.Combine(_files.Folder, $"q-{Guid.NewGuid():N}.pdf");

    // ================================================================= text to PDF

    [Fact]
    public void A4_fits_81_letters_across_and_61_lines_down()
    {
        var options = new TextPageOptions(A4);
        Assert.Equal(81, options.Columns);
        Assert.Equal(61, options.LinesPerPage);
    }

    [Fact]
    public void Lines_fill_pages_and_form_feeds_start_new_ones()
    {
        var pages = TextToPdfWriter.Paginate(["a", "b", "c", "\f", "d", "e", "\f"], 2);
        Assert.Equal([["a", "b"], ["c"], ["d", "e"]], pages);
        Assert.Single(TextToPdfWriter.Paginate([], 10));
    }

    [Fact]
    public async Task Text_becomes_pages_of_real_text()
    {
        string path = NewPath;
        var options = new TextPageOptions(A4);
        var lines = Enumerable.Range(1, 130).Select(n => $"Line {n}  café   columns stay lined up").ToList();
        int pages = await Engine.TextToPdfAsync(lines, options, path);
        Assert.Equal(3, pages); // 61 + 61 + 8

        await using var doc = await Engine.OpenAsync(path);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(A4.Width, doc.PageSizes[0].Width, 1);
        string first = await doc.GetPageTextAsync(0);
        Assert.Contains("Line 1", first, StringComparison.Ordinal);
        Assert.Contains("café", first, StringComparison.Ordinal);
        Assert.Contains("Line 130", await doc.GetPageTextAsync(2), StringComparison.Ordinal);

        // A typewriter font: every letter the same width, so "Line 1" and "Line 9" end in the same place.
        var words = await doc.GetPageWordsAsync(0);
        var one = words.First(w => w.Text == "1");
        var nine = words.First(w => w.Text == "9");
        Assert.Equal(one.Area.X, nine.Area.X, 3);
    }

    [Fact]
    public async Task Letters_beyond_Western_ones_are_embedded_and_read_back()
    {
        string path = NewPath;
        await Engine.TextToPdfAsync(["Καλημέρα κόσμε", "Привет, мир"], new TextPageOptions(A4), path);
        await using var doc = await Engine.OpenAsync(path);
        string text = await doc.GetPageTextAsync(0);
        Assert.Contains("Καλημέρα", text, StringComparison.Ordinal);
        Assert.Contains("Привет", text, StringComparison.Ordinal);
    }

    // ================================================================= headings

    /// <summary>
    /// Four pages: a big title, chapter headings, a smaller sub-heading, a two-line heading, body text, a running header
    /// bigger than the body on every page, and big page numbers.
    /// </summary>
    private string CreateReport()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var body = new XFont("Arial", 11);
        for (int n = 1; n <= 4; n++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(A4.Width);
            page.Height = XUnit.FromPoint(A4.Height);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("ACME CORPORATION", new XFont("Arial", 14), XBrushes.Gray, 72, 30, XStringFormats.TopLeft);
            g.DrawString($"{n}", new XFont("Arial", 18), XBrushes.Gray, 290, 800, XStringFormats.TopLeft);
            double y = 90;
            void Write(string text, double size)
            {
                g.DrawString(text, size == 11 ? body : new XFont("Arial", size), XBrushes.Black, 72, y, XStringFormats.TopLeft);
                y += size * 1.6;
            }

            void Body()
            {
                for (int i = 0; i < 12; i++)
                {
                    Write("This is ordinary body text that runs across the page and says very little at all.", 11);
                }
            }

            switch (n)
            {
                case 1:
                    Write("Annual Report", 26);
                    Write("Introduction", 16);
                    Body();
                    break;
                case 2:
                    Write("Results", 16);
                    Body();
                    Write("Sales", 13);
                    Body();
                    break;
                case 3:
                    Write("What we plan to do", 16);
                    Write("over the next year", 16);
                    Body();
                    break;
                default:
                    Body();
                    break;
            }
        }

        string path = NewPath;
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task Headings_become_a_tree_of_bookmarks()
    {
        await using var doc = await Engine.OpenAsync(CreateReport());
        var outline = await Engine.FindHeadingsAsync(doc);

        var title = Assert.Single(outline);
        Assert.Equal("Annual Report", title.Title);
        Assert.Equal(0, title.PageIndex);
        Assert.Equal(["Introduction", "Results", "What we plan to do over the next year"], title.Children.Select(c => c.Title));
        Assert.Equal([0, 1, 2], title.Children.Select(c => c.PageIndex));
        var sales = Assert.Single(title.Children[1].Children);
        Assert.Equal("Sales", sales.Title);
        Assert.InRange(sales.Top!.Value, 0.05, 0.95);
        Assert.True(sales.Top > title.Children[1].Top, "Sales sits lower on its page than Results");
    }

    [Fact]
    public async Task Found_headings_save_as_bookmarks()
    {
        string path = CreateReport();
        IReadOnlyList<PdfOutlineItem> outline;
        await using (var doc = await Engine.OpenAsync(path))
        {
            outline = await Engine.FindHeadingsAsync(doc);
        }

        string saved = NewPath;
        await Engine.SetOutlineAsync(new PdfInputFile(path), outline, saved);
        await using var reopened = await Engine.OpenAsync(saved);
        var read = await reopened.GetOutlineAsync();
        Assert.Equal("Annual Report", Assert.Single(read).Title);
        Assert.Equal(1, read[0].Children[1].Children[0].PageIndex);
    }

    [Fact]
    public async Task A_document_all_in_one_size_has_no_headings()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(3));
        Assert.Empty(await Engine.FindHeadingsAsync(doc));
    }

    // ================================================================= web addresses

    [Fact]
    public async Task Web_addresses_in_the_text_become_real_links()
    {
        string path = _files.CreateNavigable();
        IReadOnlyList<NewPdfLink> found;
        await using (var doc = await Engine.OpenAsync(path))
        {
            found = await WebAddressLinks.FindAsync(doc);
        }

        var link = Assert.Single(found);
        Assert.Equal(0, link.PageIndex);
        Assert.Equal("https://example.org/plain", link.Uri);

        string saved = NewPath;
        await Engine.EditLinksAsync(new PdfInputFile(path), [], found, saved);
        await using var linked = await Engine.OpenAsync(saved);
        var links = await linked.GetLinksAsync(0);
        Assert.Contains(links, l => !l.IsDetected && l.Uri == "https://example.org/plain");
        Assert.Contains(links, l => !l.IsDetected && l.Uri == "https://example.com/annot");
        Assert.Empty(await WebAddressLinks.FindAsync(linked)); // done once, nothing left to do
    }

    // ================================================================= fast web view

    [Fact]
    public async Task Fast_web_view_lays_the_file_out_for_the_web_even_when_it_is_no_smaller()
    {
        string path = _files.Create(3);
        string output = NewPath;
        var result = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.HighQuality with { FastWebView = true }, output);
        Assert.True(result.Changed);

        // A linearised file says so in its very first object.
        byte[] start = File.ReadAllBytes(output).Take(1024).ToArray();
        Assert.Contains("/Linearized", System.Text.Encoding.ASCII.GetString(start), StringComparison.Ordinal);
        await using var doc = await Engine.OpenAsync(output);
        Assert.Equal(3, doc.PageCount);
    }

    [Fact]
    public async Task Without_fast_web_view_a_file_that_cant_shrink_is_left_alone()
    {
        string path = _files.Create(1);
        string output = NewPath;
        var result = await Engine.CompressAsync(new PdfInputFile(path), CompressOptions.HighQuality, output);
        if (result.Changed)
        {
            Assert.DoesNotContain("/Linearized", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output).Take(1024).ToArray()), StringComparison.Ordinal);
        }
        else
        {
            Assert.False(File.Exists(output) && new FileInfo(output).Length > 0);
        }
    }
}
