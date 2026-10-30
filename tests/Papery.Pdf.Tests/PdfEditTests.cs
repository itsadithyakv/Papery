using Papery.Pdf.Editing;
using PdfSharp.Drawing;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Tests;

public sealed class PdfEditTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    /// <summary>A plain PDF with words on each page, optionally with a /Rotate on the pages.</summary>
    private string Create(int pages = 3, int rotate = 0)
    {
        GlobalFontSettings();
        var doc = new SharpDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(600);
            page.Height = XUnit.FromPoint(800);
            using (var g = XGraphics.FromPdfPage(page))
            {
                g.DrawString($"Original words {i + 1}", new XFont("Arial", 14), XBrushes.Black, 72, 100);
            }

            page.Rotate = rotate;
        }

        string path = Path.Combine(_files.Folder, $"edit-{Guid.NewGuid():N}.pdf");
        doc.Save(path);
        return path;
    }

    private static void GlobalFontSettings() => PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    private static async Task<string> TextOf(string path, int page)
    {
        await using var doc = await Engine.OpenAsync(path);
        return await doc.GetPageTextAsync(page);
    }

    private static async Task<int> InkInBox(string path, int page, NormalizedRect box)
    {
        await using var doc = await Engine.OpenAsync(path);
        const int Width = 600;
        int height = (int)(Width * doc.PageSizes[page].Height / doc.PageSizes[page].Width);
        var bitmap = await doc.RenderPageAsync(page, Width, height, PdfRenderOptions.Annotations, PdfWorkPriority.Visible);
        int ink = 0;
        for (int y = (int)(box.Y * height); y < (int)(box.Bottom * height); y++)
        {
            for (int x = (int)(box.X * Width); x < (int)(box.Right * Width); x++)
            {
                if (bitmap.Pixels[(y * bitmap.Stride) + (x * 4) + 1] < 200)
                {
                    ink++;
                }
            }
        }

        return ink;
    }

    // ================================================================= sums

    [Fact]
    public void Numbers_count_only_stamped_pages()
    {
        Assert.Equal([(1, 1), (2, 2)], PageStampPlan.Numbering(3, null, skipFirst: true, startNumber: 1));
        Assert.Equal([(0, 5), (2, 6)], PageStampPlan.Numbering(3, [2, 0, 0, 9], skipFirst: false, startNumber: 5));
        Assert.Empty(PageStampPlan.Numbering(1, null, skipFirst: true, startNumber: 1));
    }

    [Fact]
    public void Tokens_fill_in()
    {
        var day = new DateTime(2026, 10, 30);
        Assert.Equal("Page 3 of 10", PageStampPlan.Resolve("Page {page} of {pages}", 3, 10, day));
        Assert.Equal("ACME-000042", PageStampPlan.Resolve("ACME-{page:6}", 42, 99, day));
        Assert.Contains("2026", PageStampPlan.Resolve("{date}", 1, 1, day), StringComparison.Ordinal);
        Assert.Equal("no tokens {other}", PageStampPlan.Resolve("no tokens {other}", 1, 1, day));
    }

    [Theory]
    [InlineData(PageStampPosition.TopLeft, 30, 25)]
    [InlineData(PageStampPosition.BottomRight, 570, 775)]
    [InlineData(PageStampPosition.Middle, 300, 400)]
    public void Stamps_sit_inside_the_margin(PageStampPosition position, double across, double down)
    {
        var centre = PageStampPlan.Centre(position, 600, 800, 40, 30, 10);
        Assert.Equal(across, centre.Across);
        Assert.Equal(down, centre.Down);
    }

    // ================================================================= stamping

    [Fact]
    public async Task Page_numbers_join_the_page_and_old_words_stay()
    {
        string path = Create();

        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "Page {page} of {pages}" }, path);

        for (int i = 0; i < 3; i++)
        {
            string text = await TextOf(path, i);
            Assert.Contains($"Page {i + 1} of 3", text, StringComparison.Ordinal);
            Assert.Contains($"Original words {i + 1}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Skipping_the_first_page_leaves_it_alone()
    {
        string path = Create();

        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "No. {page}", SkipFirst = true }, path);

        Assert.DoesNotContain("No.", await TextOf(path, 0), StringComparison.Ordinal);
        Assert.Contains("No. 1", await TextOf(path, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stamps_land_where_asked_on_a_rotated_page()
    {
        string path = Create(pages: 1, rotate: 90);

        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "CORNER", Position = PageStampPosition.TopLeft, FontSize = 18, Color = new PdfColor(0, 0, 0) }, path);

        Assert.True(await InkInBox(path, 0, new NormalizedRect(0, 0, 0.3, 0.12)) > 20, "the stamp should be at the top-left as displayed");
        Assert.Equal(0, await InkInBox(path, 0, new NormalizedRect(0.7, 0.88, 0.3, 0.12)));
    }

    [Fact]
    public async Task A_watermark_is_faint_and_turned()
    {
        string path = Create(pages: 1);

        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "DRAFT", Position = PageStampPosition.Middle, FontSize = 90, Color = new PdfColor(0, 0, 0), Opacity = 0.25, AngleDegrees = 45 }, path);

        await using var doc = await Engine.OpenAsync(path);
        var bitmap = await doc.RenderPageAsync(0, 600, 800, PdfRenderOptions.None, PdfWorkPriority.Visible);
        byte darkest = 255;
        for (int y = 200; y < 600; y++)
        {
            for (int x = 100; x < 500; x++)
            {
                darkest = Math.Min(darkest, bitmap.Pixels[(y * bitmap.Stride) + (x * 4) + 1]);
            }
        }

        Assert.InRange(darkest, (byte)150, (byte)220); // there, but see-through
        Assert.Contains("Original words 1", await doc.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_stamp_goes_on_every_page()
    {
        string path = Create(pages: 2);
        var black = new PdfBitmap(4, 4, 16, new byte[4 * 4 * 4].Select((_, i) => (byte)(i % 4 == 3 ? 255 : 0)).ToArray());

        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Picture = black, PictureWidth = 100, Position = PageStampPosition.BottomRight }, path);

        Assert.True(await InkInBox(path, 1, new NormalizedRect(0.75, 0.8, 0.2, 0.15)) > 100);
    }

    [Fact]
    public async Task A_failed_stamp_leaves_the_file_alone()
    {
        string path = Create();
        byte[] before = await File.ReadAllBytesAsync(path);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "x" }, path, cancelled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    // ================================================================= links

    [Fact]
    public async Task Links_can_be_added_changed_and_removed()
    {
        string path = Create();
        var area = new NormalizedRect(0.1, 0.1, 0.3, 0.05);

        await Engine.EditLinksAsync(new PdfInputFile(path), [], [new NewPdfLink(0, area, "https://example.com/", null), new NewPdfLink(1, area, null, 2)], path);

        await using (var doc = await Engine.OpenAsync(path))
        {
            var web = Assert.Single(await doc.GetLinksAsync(0), l => !l.IsDetected);
            Assert.Equal("https://example.com/", web.Uri);
            Assert.InRange(web.Area.X, 0.09, 0.11);
            Assert.Equal(2, Assert.Single(await doc.GetLinksAsync(1)).TargetPage);
        }

        await Engine.EditLinksAsync(new PdfInputFile(path), [new PdfLinkRemoval(0, area)], [new NewPdfLink(0, area, "https://example.org/", null)], path);
        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Equal("https://example.org/", Assert.Single(await doc.GetLinksAsync(0)).Uri);
        }

        await Engine.EditLinksAsync(new PdfInputFile(path), [new PdfLinkRemoval(0, area)], [], path);
        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Empty(await doc.GetLinksAsync(0));
            Assert.Single(await doc.GetLinksAsync(1)); // others stay
        }
    }

    // ================================================================= bookmarks

    private static OutlineEditor Sample() => new(
    [
        new PdfOutlineItem("One", 0, [new PdfOutlineItem("One A", 0, []), new PdfOutlineItem("One B", 1, [])]),
        new PdfOutlineItem("Two", 1, []),
        new PdfOutlineItem("Three", 2, []),
    ]);

    private static string[] Shape(OutlineEditor editor) => editor.Rows.Select(r => new string('-', r.Depth) + r.Title).ToArray();

    [Fact]
    public void Moving_a_bookmark_brings_its_children()
    {
        var editor = Sample();

        int now = editor.MoveDown(0);

        Assert.Equal(1, now);
        Assert.Equal(["Two", "One", "-One A", "-One B", "Three"], Shape(editor));
        Assert.Equal(0, editor.MoveUp(1));
        Assert.Equal(["One", "-One A", "-One B", "Two", "Three"], Shape(editor));
    }

    [Fact]
    public void A_bookmark_stops_at_the_edge_of_its_siblings()
    {
        var editor = Sample();

        Assert.Equal(0, editor.MoveUp(0));
        Assert.Equal(1, editor.MoveUp(1)); // "One A" is first under "One"
        Assert.Equal(2, editor.MoveDown(2)); // "One B" is last under "One"
        Assert.Equal(["One", "-One A", "-One B", "Two", "Three"], Shape(editor));
    }

    [Fact]
    public void Indenting_needs_something_to_sit_under()
    {
        var editor = Sample();

        editor.Indent(0);
        Assert.Equal(0, editor.Rows[0].Depth);

        editor.Indent(3);
        Assert.Equal(["One", "-One A", "-One B", "-Two", "Three"], Shape(editor));

        editor.Outdent(3);
        Assert.Equal(["One", "-One A", "-One B", "Two", "Three"], Shape(editor));
    }

    [Fact]
    public void The_tree_comes_back_the_same_shape()
    {
        var tree = Sample().ToTree();

        Assert.Equal(3, tree.Count);
        Assert.Equal(["One A", "One B"], tree[0].Children.Select(c => c.Title));
        Assert.Empty(tree[1].Children);
    }

    [Fact]
    public void Bookmarks_with_no_page_survive_saving()
    {
        var tree = new OutlineEditor(
        [
            new PdfOutlineItem("Parent", 3, [new PdfOutlineItem("Child", null, [])]),
            new PdfOutlineItem("Alone", null, []),
        ]).ToTree();

        Assert.Equal(3, tree[0].Children[0].PageIndex);
        Assert.Equal(0, tree[1].PageIndex);
    }

    [Fact]
    public void Removing_takes_the_children_too_and_adding_goes_after_them()
    {
        var editor = Sample();

        Assert.Equal(3, editor.Add(0, "New", 2));
        Assert.Equal(["One", "-One A", "-One B", "New", "Two", "Three"], Shape(editor));

        editor.Remove(0);
        Assert.Equal(["New", "Two", "Three"], Shape(editor));
    }

    [Fact]
    public async Task Bookmarks_save_read_back_and_can_all_be_removed()
    {
        string path = Create();

        await Engine.SetOutlineAsync(new PdfInputFile(path), Sample().ToTree(), path);
        await using (var doc = await Engine.OpenAsync(path))
        {
            var outline = await doc.GetOutlineAsync();
            Assert.Equal(["One", "Two", "Three"], outline.Select(o => o.Title));
            Assert.Equal(2, outline[0].Children.Count);
            Assert.Equal(2, outline[2].PageIndex);
        }

        await Engine.SetOutlineAsync(new PdfInputFile(path), [], path);
        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Empty(await doc.GetOutlineAsync());
        }
    }

    /// <summary>A hand-written PDF whose bookmark has no target at all, as some real files do.</summary>
    private string CreateWithTargetlessBookmark()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (Nowhere) /Parent 4 0 R >>",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int table = text.Length;
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        string path = Path.Combine(_files.Folder, $"odd-outline-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [Fact]
    public async Task Bookmarks_can_be_replaced_even_when_the_old_ones_are_odd()
    {
        string path = CreateWithTargetlessBookmark();

        await Engine.SetOutlineAsync(new PdfInputFile(path), [new PdfOutlineItem("Fresh", 0, [])], path);

        await using var doc = await Engine.OpenAsync(path);
        Assert.Equal("Fresh", Assert.Single(await doc.GetOutlineAsync()).Title);
    }

    // ================================================================= undo for whole-file changes

    [Fact]
    public async Task A_copy_can_be_put_back()
    {
        string path = Create();
        string copy = path + ".copy";
        File.Copy(path, copy);
        await Engine.StampPagesAsync(new PdfInputFile(path), new PdfPageStamp { Text = "ADDED" }, path);
        Assert.Contains("ADDED", await TextOf(path, 0), StringComparison.Ordinal);

        await Engine.RestoreCopyAsync(copy, null, path);

        Assert.DoesNotContain("ADDED", await TextOf(path, 0), StringComparison.Ordinal);
    }

    // ================================================================= added text, pictures and whiteout

    [Fact]
    public async Task Added_text_keeps_its_font_and_alignment_through_saving()
    {
        string path = Create(pages: 1);
        var draft = new PdfAnnotationDraft(PdfAnnotationKind.TextBox, new PdfColor(0, 0, 0), [new NormalizedRect(0.1, 0.5, 0.8, 0.1)], Text: "Right side", FontSize: 16, Font: PdfTextFont.Serif, Bold: true, Italic: true, Align: PdfTextAlign.Right);
        string name;
        await using (var doc = await Engine.OpenAsync(path))
        {
            name = (await doc.AddAnnotationAsync(0, draft)).Name;
            await doc.SaveAsync(path);
        }

        Assert.True(await InkInBox(path, 0, new NormalizedRect(0.6, 0.5, 0.3, 0.1)) > 20, "right-aligned words should be on the right");
        Assert.Equal(0, await InkInBox(path, 0, new NormalizedRect(0.1, 0.5, 0.3, 0.1)));

        await using var reopened = await Engine.OpenAsync(path);
        var back = await reopened.GetAnnotationDraftAsync(0, name);
        Assert.NotNull(back);
        Assert.Equal(PdfTextFont.Serif, back.Font);
        Assert.True(back.Bold && back.Italic);
        Assert.Equal(PdfTextAlign.Right, back.Align);
    }

    [Theory]
    [InlineData("First\rSecond")]
    [InlineData("First\r\nSecond")]
    [InlineData("First\nSecond")]
    public async Task Line_breaks_of_every_kind_make_new_lines(string words)
    {
        string path = Create(pages: 1);
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.TextBox, new PdfColor(0, 0, 0), [new NormalizedRect(0.1, 0.5, 0.8, 0.2)], Text: words, FontSize: 20));
            await doc.SaveAsync(path);
        }

        // "First" and "Second" on one line would leave the second line's space empty.
        Assert.True(await InkInBox(path, 0, new NormalizedRect(0.1, 0.5, 0.3, 0.035)) > 0);
        Assert.True(await InkInBox(path, 0, new NormalizedRect(0.1, 0.54, 0.3, 0.04)) > 0);
    }

    [Fact]
    public async Task Text_can_be_rewritten_in_place()
    {
        string path = Create(pages: 1);
        await using var doc = await Engine.OpenAsync(path);
        var draft = new PdfAnnotationDraft(PdfAnnotationKind.TextBox, new PdfColor(0, 0, 0), [new NormalizedRect(0.1, 0.5, 0.8, 0.1)], Text: "Before");
        var added = await doc.AddAnnotationAsync(0, draft);

        Assert.True(await doc.ChangeAnnotationAsync(0, added.Name, draft with { Text = "After", Bold = true }));

        var info = Assert.Single(await doc.GetAnnotationsAsync(0));
        Assert.Equal("After", info.Text);
    }

    [Fact]
    public async Task Whiteout_hides_what_it_covers_but_the_words_stay_in_the_file()
    {
        string path = Create(pages: 1);
        var words = new NormalizedRect(0.1, 0.1, 0.3, 0.04);
        Assert.True(await InkInBox(path, 0, words) > 0);
        await using (var doc = await Engine.OpenAsync(path))
        {
            var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Whiteout, new PdfColor(255, 255, 255), [new NormalizedRect(0.05, 0.08, 0.5, 0.08)]));
            Assert.Equal(PdfAnnotationKind.Whiteout, added.Kind);
            await doc.SaveAsync(path);
        }

        Assert.Equal(0, await InkInBox(path, 0, words));

        // This is why the UI says it only covers: real removal is the Redact tool's job.
        Assert.Contains("Original words", await TextOf(path, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_can_be_placed_moved_and_saved()
    {
        string path = Create(pages: 1);
        var black = new PdfBitmap(4, 4, 16, new byte[4 * 4 * 4].Select((_, i) => (byte)(i % 4 == 3 ? 255 : 0)).ToArray());
        var to = new NormalizedRect(0.6, 0.6, 0.2, 0.15);
        await using (var doc = await Engine.OpenAsync(path))
        {
            var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Image, new PdfColor(0, 0, 0), [new NormalizedRect(0.1, 0.3, 0.1, 0.1)], Picture: black));
            Assert.Equal(PdfAnnotationKind.Image, added.Kind);
            await doc.MoveAnnotationAsync(0, added.Name, to);
            await doc.SaveAsync(path);
        }

        Assert.Equal(0, await InkInBox(path, 0, new NormalizedRect(0.1, 0.3, 0.1, 0.1)));
        Assert.True(await InkInBox(path, 0, new NormalizedRect(0.62, 0.62, 0.15, 0.1)) > 100);
    }
}
