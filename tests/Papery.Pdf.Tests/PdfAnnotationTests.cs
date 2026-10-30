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
    ];

    private static PdfAnnotationDraft Draft(PdfAnnotationKind kind) => kind switch
    {
        PdfAnnotationKind.Pen or PdfAnnotationKind.Marker or PdfAnnotationKind.Line or PdfAnnotationKind.Arrow =>
            new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.5, 0.5), (0.7, 0.6)), Width: 3),
        PdfAnnotationKind.Polygon => new(kind, Red, Strokes: Stroke((0.3, 0.4), (0.7, 0.4), (0.5, 0.6)), Width: 3),
        PdfAnnotationKind.Measure => new(kind, Red, Strokes: Stroke((0.3, 0.5), (0.7, 0.5)), Width: 2, Text: "12.3 cm"),
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
}
