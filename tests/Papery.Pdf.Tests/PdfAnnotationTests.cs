using Papery.Pdf.Editing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Tests;

public sealed class PdfAnnotationTests : IDisposable
{
    private static readonly PdfColor Red = new(220, 30, 30);
    private static readonly NormalizedRect Area = new(0.3, 0.4, 0.4, 0.2);

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private static IReadOnlyList<IReadOnlyList<PagePoint>> Stroke(params (double X, double Y)[] points) =>
        [points.Select(p => new PagePoint(p.X, p.Y)).ToArray()];

    public static TheoryData<PdfAnnotationKind> DrawnKinds =>
    [
        PdfAnnotationKind.Highlight, PdfAnnotationKind.Underline, PdfAnnotationKind.Squiggly, PdfAnnotationKind.StrikeOut,
        PdfAnnotationKind.Note, PdfAnnotationKind.Pen, PdfAnnotationKind.Marker, PdfAnnotationKind.Rectangle, PdfAnnotationKind.Ellipse,
        PdfAnnotationKind.Line, PdfAnnotationKind.Arrow, PdfAnnotationKind.Polygon, PdfAnnotationKind.Cloud,
        PdfAnnotationKind.TextBox, PdfAnnotationKind.Callout, PdfAnnotationKind.Stamp, PdfAnnotationKind.Measure,
        PdfAnnotationKind.Area, PdfAnnotationKind.Perimeter,
    ];

    private static PdfAnnotationDraft Draft(PdfAnnotationKind kind) => kind switch
    {
        PdfAnnotationKind.Pen or PdfAnnotationKind.Marker or PdfAnnotationKind.Line or PdfAnnotationKind.Arrow =>
            new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.5, 0.5), (0.7, 0.6)), Width: 3),
        PdfAnnotationKind.Polygon => new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.7, 0.4), (0.5, 0.6)), Width: 3),
        PdfAnnotationKind.Measure => new(kind, Red, Strokes: Stroke((0.3, 0.5), (0.7, 0.5)), Width: 2, Text: "12.3 cm"),
        PdfAnnotationKind.Area => new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.7, 0.4), (0.7, 0.6), (0.3, 0.6)), Width: 2, Text: "49.9 cm²", FontSize: 11),
        PdfAnnotationKind.Perimeter => new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.7, 0.4), (0.5, 0.6)), Width: 2, Text: "15.7 cm", FontSize: 11),
        PdfAnnotationKind.TextBox => new(kind, Red, [Area], Text: "Please check these numbers", FontSize: 16),
        PdfAnnotationKind.Callout => new(kind, Red, [Area], Stroke((0.3, 0.5), (0.1, 0.8)), Text: "Look here", FontSize: 16),
        PdfAnnotationKind.Stamp => new(kind, Red, [Area], Text: "APPROVED"),
        PdfAnnotationKind.Note => new(kind, Red, [Area], Text: "A note"),
        _ => new(kind, Red, [Area], Width: 3),
    };

    /// <summary>Counts red-ish pixels on the rendered page.</summary>
    private static async Task<int> RedPixels(PdfDocument doc, int page = 0)
    {
        var b = await doc.RenderPageAsync(page, 400, 566);
        int count = 0;
        for (int i = 0; i < b.Pixels.Length; i += 4)
        {
            if (b.Pixels[i + 2] > 150 && b.Pixels[i] < 150 && b.Pixels[i + 1] < 150)
            {
                count++;
            }
        }

        return count;
    }

    [Theory]
    [MemberData(nameof(DrawnKinds))]
    public async Task Every_kind_draws_saves_and_comes_back(PdfAnnotationKind kind)
    {
        string path = _files.Create(1, "Hello");
        string output = Path.Combine(_files.Folder, "out.pdf");
        await using (var doc = await Engine.OpenAsync(path))
        {
            Assert.Equal(0, await RedPixels(doc));
            var added = await doc.AddAnnotationAsync(0, Draft(kind));
            Assert.Equal(kind, added.Kind);
            Assert.True(await RedPixels(doc) > 0, "it shows on the page straight away");
            Assert.True(doc.HasAnnotationChanges);
            await doc.SaveAsync(output);
        }

        await using var saved = await Engine.OpenAsync(output);
        Assert.True(await RedPixels(saved) > 0, "it is still there after saving");
        var info = Assert.Single(await saved.GetAnnotationsAsync(0));
        Assert.Equal(kind, info.Kind);

        // A real annotation with its own appearance is what makes Edge and Adobe draw it the same way.
        using var sharp = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var annotation = sharp.Pages[0].Annotations[0];
        Assert.True(annotation.Elements.ContainsKey("/AP"), $"{kind} has no appearance stream");
    }

    [Fact]
    public async Task Saving_is_exact_about_text_marks_and_their_words()
    {
        string path = _files.Create(1, "Hello");
        string output = Path.Combine(_files.Folder, "out.pdf");
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Highlight, new PdfColor(255, 220, 0, 110), [Area, new NormalizedRect(0.3, 0.65, 0.4, 0.03)], Text: "Check", Author: "Sam"));
            await doc.SaveAsync(output);
        }

        await using var saved = await Engine.OpenAsync(output);
        var info = Assert.Single(await saved.GetAnnotationsAsync(0));
        Assert.Equal("Check", info.Text);
        Assert.Equal("Sam", info.Author);
        using var sharp = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(8 * 2, sharp.Pages[0].Annotations[0].Elements.GetArray("/QuadPoints")!.Elements.Count);
    }

    [Fact]
    public async Task A_highlight_leaves_the_text_readable()
    {
        string path = _files.Create(1, "Hello");
        await using var doc = await Engine.OpenAsync(path);
        var word = (await doc.FindOnPageAsync(0, "Hello"))[0].Areas[0];

        await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Highlight, new PdfColor(255, 220, 0, 110), [word]));

        var b = await doc.RenderPageAsync(0, 1200, 1698);
        int x0 = (int)(word.X * 1200), x1 = (int)(word.Right * 1200), y0 = (int)(word.Y * 1698), y1 = (int)(word.Bottom * 1698);
        int dark = 0, tinted = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * b.Stride) + (x * 4);
                dark += b.Pixels[i + 2] < 120 ? 1 : 0;
                tinted += b.Pixels[i] < 220 && b.Pixels[i + 2] > 200 ? 1 : 0;
            }
        }

        Assert.True(dark > 50, "the letters still show through");
        Assert.True(tinted > 500, "the background is tinted");
    }

    [Fact]
    public async Task Deleting_hides_and_undo_brings_it_back_and_saving_removes_it()
    {
        string path = _files.Create(1, "Hello");
        string output = Path.Combine(_files.Folder, "out.pdf");
        await using (var doc = await Engine.OpenAsync(path))
        {
            var added = await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.Rectangle));

            Assert.True(await doc.SetAnnotationHiddenAsync(0, added.Name, true));
            Assert.Equal(0, await RedPixels(doc));
            Assert.Empty(await doc.GetAnnotationsAsync(0));

            Assert.True(await doc.SetAnnotationHiddenAsync(0, added.Name, false));
            Assert.True(await RedPixels(doc) > 0);

            Assert.True(await doc.SetAnnotationHiddenAsync(0, added.Name, true));
            await doc.SaveAsync(output);
        }

        using var sharp = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(0, sharp.Pages[0].Annotations.Count);
    }

    [Fact]
    public async Task Recolouring_and_moving_keep_the_same_annotation()
    {
        string path = _files.Create(1, "Hello");
        await using var doc = await Engine.OpenAsync(path);
        var added = await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.Rectangle));

        Assert.True(await doc.RecolorAnnotationAsync(0, added.Name, new PdfColor(0, 0, 200)));
        Assert.Equal(0, await RedPixels(doc));

        var target = new NormalizedRect(0.1, 0.1, 0.2, 0.1);
        Assert.True(await doc.MoveAnnotationAsync(0, added.Name, target));
        var moved = Assert.Single(await doc.GetAnnotationsAsync(0));
        Assert.Equal(added.Name, moved.Name);
        Assert.InRange(moved.Bounds.X, 0.08, 0.12);
        Assert.InRange(moved.Bounds.Y, 0.08, 0.12);
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Rectangle)]
    [InlineData(PdfAnnotationKind.Pen)]
    [InlineData(PdfAnnotationKind.Stamp)]
    [InlineData(PdfAnnotationKind.Highlight)]
    [InlineData(PdfAnnotationKind.Area)]
    [InlineData(PdfAnnotationKind.Perimeter)]
    public async Task Papery_annotations_can_still_be_recoloured_after_reopening(PdfAnnotationKind kind)
    {
        string path = _files.Create(1, "Hello");
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, Draft(kind));
            await doc.SaveAsync(path);
        }

        await using var reopened = await Engine.OpenAsync(path);
        var info = Assert.Single(await reopened.GetAnnotationsAsync(0));
        Assert.Equal(Red, info.Color);

        Assert.True(await reopened.RecolorAnnotationAsync(0, info.Name, new PdfColor(0, 0, 200)));

        Assert.Equal(0, await RedPixels(reopened));
    }

    [Fact]
    public async Task Words_of_a_saved_text_box_can_be_changed_after_reopening()
    {
        string path = _files.Create(1, "Hello");
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.TextBox));
            await doc.SaveAsync(path);
        }

        await using var reopened = await Engine.OpenAsync(path);
        var info = Assert.Single(await reopened.GetAnnotationsAsync(0));
        Assert.True(await reopened.SetAnnotationTextAsync(0, info.Name, "New words"));
        Assert.Equal("New words", Assert.Single(await reopened.GetAnnotationsAsync(0)).Text);
    }

    [Fact]
    public async Task Annotations_on_a_turned_page_land_where_the_user_drew_them()
    {
        string path = _files.Create(1, "Hello");
        string turned = Path.Combine(_files.Folder, "turned.pdf");
        await Engine.ComposeAsync(new PdfComposeRequest([new PdfInputFile(path)], [new PageRef(0, 0, QuarterTurns: 1)], EditInPlaceOf: 0), turned);
        await using var doc = await Engine.OpenAsync(turned);

        var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.TextBox, Red, [new NormalizedRect(0.1, 0.1, 0.5, 0.2)], Text: "Upright", FontSize: 20));

        Assert.InRange(added.Bounds.X, 0.08, 0.12);
        Assert.InRange(added.Bounds.Y, 0.08, 0.12);
        Assert.True(await RedPixels(doc) > 0);
    }

    [Fact]
    public async Task A_password_stays_on_after_saving_annotations()
    {
        string path = _files.Create(1, "Secret", password: "pw");
        await using (var doc = await Engine.OpenAsync(path, "pw"))
        {
            await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.Highlight));
            await doc.SaveAsync(path);
        }

        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(path));
        Assert.Equal(PdfOpenError.PasswordRequired, ex.Error);
        await using var reopened = await Engine.OpenAsync(path, "pw");
        Assert.Single(await reopened.GetAnnotationsAsync(0));
    }

    [Fact]
    public async Task Links_are_not_listed_as_annotations()
    {
        await using var doc = await Engine.OpenAsync(_files.CreateNavigable());
        Assert.Empty(await doc.GetAnnotationsAsync(0));
    }

    [Fact]
    public async Task Non_western_text_is_embedded_and_drawn()
    {
        string path = _files.Create(1, "Hello");
        await using var doc = await Engine.OpenAsync(path);

        await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.TextBox, Red, [Area], Text: "Привет мир", FontSize: 24));

        Assert.True(await RedPixels(doc) > 0);
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Area, "/Polygon", 4)]
    [InlineData(PdfAnnotationKind.Perimeter, "/PolyLine", 3)]
    public async Task Measured_shapes_save_as_real_shapes_with_their_corners_and_total(PdfAnnotationKind kind, string subtype, int corners)
    {
        string path = _files.Create(1, "Hello");
        string output = Path.Combine(_files.Folder, "out.pdf");
        var draft = Draft(kind);
        PdfPageSize size;
        await using (var doc = await Engine.OpenAsync(path))
        {
            size = doc.PageSizes[0];
            await doc.AddAnnotationAsync(0, draft);
            await doc.SaveAsync(output);
        }

        // What other apps read: the shape, its corners in page space, its total and its scale.
        using (var sharp = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var annotation = sharp.Pages[0].Annotations[0];
            Assert.Equal(subtype, annotation.Elements.GetName("/Subtype"));
            Assert.Equal(draft.Text, annotation.Elements.GetString("/Contents"));
            Assert.False(annotation.Elements.ContainsKey("/InkList"));
            Assert.True(annotation.Elements.ContainsKey("/Measure"));
            var vertices = annotation.Elements.GetArray("/Vertices")!;
            Assert.Equal(corners * 2, vertices.Elements.Count);
            Assert.Equal(0.3 * size.Width, vertices.Elements.GetReal(0), 1);
            Assert.Equal((1 - 0.4) * size.Height, vertices.Elements.GetReal(1), 1);
        }

        // What Papery reads: the same kind and total, and the corners to draw it again from.
        await using var saved = await Engine.OpenAsync(output);
        var info = Assert.Single(await saved.GetAnnotationsAsync(0));
        Assert.Equal(kind, info.Kind);
        Assert.Equal(draft.Text, info.Text);
        Assert.True(await RedPixels(saved) > 0);
        var back = await saved.GetAnnotationDraftAsync(0, info.Name);
        Assert.Equal(draft.Strokes![0], back!.Strokes![0]);
    }

    [Fact]
    public async Task A_measured_shape_moved_after_reopening_is_still_a_real_shape()
    {
        string path = _files.Create(1, "Hello");
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.Area));
            await doc.SaveAsync(path);
        }

        await using (var reopened = await Engine.OpenAsync(path))
        {
            var info = Assert.Single(await reopened.GetAnnotationsAsync(0));
            Assert.True(await reopened.MoveAnnotationAsync(0, info.Name, info.Bounds with { X = info.Bounds.X - 0.2 }));
            await reopened.SaveAsync(path);
        }

        using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(1, sharp.Pages[0].Annotations.Count);
        var annotation = sharp.Pages[0].Annotations[0];
        Assert.Equal("/Polygon", annotation.Elements.GetName("/Subtype"));
        Assert.True(annotation.Elements.GetArray("/Vertices")!.Elements.GetReal(0) < 0.2 * 595, "it moved left");
    }

    [Fact]
    public async Task A_locked_file_keeps_measured_shapes_that_read_back()
    {
        string path = _files.Create(1, "Secret", password: "pw");
        await using (var doc = await Engine.OpenAsync(path, "pw"))
        {
            await doc.AddAnnotationAsync(0, Draft(PdfAnnotationKind.Perimeter));
            await doc.SaveAsync(path);
        }

        await using var reopened = await Engine.OpenAsync(path, "pw");
        var info = Assert.Single(await reopened.GetAnnotationsAsync(0));
        Assert.Equal(PdfAnnotationKind.Perimeter, info.Kind);
        Assert.Equal("15.7 cm", info.Text);
        Assert.True(await RedPixels(reopened) > 0);
    }
}
