using Papery.Pdf.Editing;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Tests;

public sealed class PdfFormTests : IDisposable
{
    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    /// <summary>A form with every kind of field.</summary>
    private string CreateForm()
    {
        var doc = new SharpDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var form = new AcroFormBuilder(doc);
        form.AddText(page, "name", new XRect(72, 100, 300, 22));
        form.AddText(page, "address", new XRect(72, 140, 300, 66), multiline: true);
        form.AddCombo(page, "country", new XRect(72, 220, 200, 22), ["India", "Japan", "France"], "Japan");
        form.AddCheckBox(page, "agree", new XRect(72, 260, 14, 14), isChecked: false);
        form.AddRadioGroup(page, "size", ["Small", "Large"], i => new XRect(72 + (i * 100), 300, 14, 14), selected: "Small");
        form.AddSignature(page, "sign", new XRect(72, 340, 200, 50));
        string path = Path.Combine(_files.Folder, "form.pdf");
        doc.Save(path);
        return path;
    }

    private static PdfFormField Field(IReadOnlyList<PdfFormField> fields, string name) => fields.First(f => f.Name == name);

    [Fact]
    public async Task All_kinds_of_field_are_read()
    {
        await using var doc = await Engine.OpenAsync(CreateForm());

        var fields = await doc.GetFormFieldsAsync(0);

        Assert.Equal(PdfFormFieldKind.Text, Field(fields, "name").Kind);
        Assert.True(Field(fields, "address").IsMultiline);
        var country = Field(fields, "country");
        Assert.Equal(PdfFormFieldKind.ComboBox, country.Kind);
        Assert.Equal(["India", "Japan", "France"], country.Options);
        Assert.Equal(1, country.SelectedOption);
        Assert.False(Field(fields, "agree").IsChecked);
        Assert.Equal(2, fields.Count(f => f.Name == "size"));
        Assert.True(fields.First(f => f.Name == "size").IsChecked);
        Assert.Equal(PdfFormFieldKind.Signature, Field(fields, "sign").Kind);
        Assert.InRange(Field(fields, "name").Bounds.X, 0.11, 0.13);
    }

    [Fact]
    public async Task Answers_save_and_read_back_and_show_on_the_page()
    {
        string path = CreateForm();
        await using (var doc = await Engine.OpenAsync(path))
        {
            var fields = await doc.GetFormFieldsAsync(0);
            await doc.SetFieldTextAsync(0, Field(fields, "name").Index, "Adi Vinod");
            await doc.SetFieldTextAsync(0, Field(fields, "address").Index, "12 Harbour Street");
            await doc.SetFieldOptionAsync(0, Field(fields, "country").Index, 2);
            await doc.SetFieldCheckedAsync(0, Field(fields, "agree").Index, true);
            await doc.SetFieldCheckedAsync(0, fields.Last(f => f.Name == "size").Index, true);
            Assert.True(doc.HasAnnotationChanges);
            await doc.SaveAsync(path);
        }

        await using var saved = await Engine.OpenAsync(path);
        var after = await saved.GetFormFieldsAsync(0);
        Assert.Equal("Adi Vinod", Field(after, "name").Value);
        Assert.Equal("12 Harbour Street", Field(after, "address").Value);
        Assert.Equal(2, Field(after, "country").SelectedOption);
        Assert.True(Field(after, "agree").IsChecked);
        Assert.False(after.First(f => f.Name == "size").IsChecked);
        Assert.True(after.Last(f => f.Name == "size").IsChecked);

        // Written into the appearance too, so any viewer shows the answer: the field has dark letters.
        var bounds = Field(after, "name").Bounds;
        var page = await saved.RenderPageAsync(0, 1000, 1414);
        int dark = 0;
        for (int y = (int)(bounds.Y * 1414); y < bounds.Bottom * 1414; y++)
        {
            for (int x = (int)(bounds.X * 1000); x < bounds.Right * 1000; x++)
            {
                dark += page.Pixels[(y * page.Stride) + (x * 4)] < 100 ? 1 : 0;
            }
        }

        Assert.True(dark > 50, $"only {dark} dark pixels in the field");
    }

    [Fact]
    public async Task A_text_field_can_be_emptied_and_unticked()
    {
        string path = CreateForm();
        await using var doc = await Engine.OpenAsync(path);
        var fields = await doc.GetFormFieldsAsync(0);

        await doc.SetFieldTextAsync(0, Field(fields, "name").Index, "Temp");
        await doc.SetFieldTextAsync(0, Field(fields, "name").Index, string.Empty);
        await doc.SetFieldCheckedAsync(0, Field(fields, "agree").Index, true);
        await doc.SetFieldCheckedAsync(0, Field(fields, "agree").Index, false);

        var after = await doc.GetFormFieldsAsync(0);
        Assert.Equal(string.Empty, Field(after, "name").Value);
        Assert.False(Field(after, "agree").IsChecked);
    }

    [Fact]
    public async Task Fields_on_a_page_without_a_form_are_none()
    {
        await using var doc = await Engine.OpenAsync(_files.Create(1));
        Assert.Empty(await doc.GetFormFieldsAsync(0));
    }

    [Fact]
    public async Task Flatten_keeps_the_look_and_removes_the_fields()
    {
        string path = CreateForm();
        await using (var doc = await Engine.OpenAsync(path))
        {
            var fields = await doc.GetFormFieldsAsync(0);
            await doc.SetFieldTextAsync(0, Field(fields, "name").Index, "Permanent Answer");
            await doc.SaveAsync(path);
        }

        string output = Path.Combine(_files.Folder, "flat.pdf");
        await Engine.FlattenAsync(new PdfInputFile(path), output);

        await using var flat = await Engine.OpenAsync(output);
        Assert.Empty(await flat.GetFormFieldsAsync(0));
        Assert.Contains("Permanent Answer", await flat.GetPageTextAsync(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signature_picture_is_see_through_where_it_is_transparent()
    {
        string path = _files.Create(1, "Hello");
        await using var doc = await Engine.OpenAsync(path);

        // Left half opaque blue, right half fully transparent.
        var pixels = new byte[100 * 50 * 4];
        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                int i = ((y * 100) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)200, (byte)0, (byte)0, (byte)255);
            }
        }

        var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(
            PdfAnnotationKind.Signature,
            new PdfColor(0, 0, 0),
            [new NormalizedRect(0.2, 0.5, 0.4, 0.1)],
            Picture: new PdfBitmap(100, 50, 400, pixels)));

        Assert.Equal(PdfAnnotationKind.Signature, added.Kind);
        var page = await doc.RenderPageAsync(0, 500, 700);
        (byte B, byte R) At(double fx, double fy)
        {
            int i = ((int)(fy * 700) * page.Stride) + ((int)(fx * 500) * 4);
            return (page.Pixels[i], page.Pixels[i + 2]);
        }

        Assert.True(At(0.3, 0.55) is { B: > 150, R: < 80 }, "the opaque half shows blue");
        Assert.True(At(0.5, 0.55) is { B: > 240, R: > 240 }, "the transparent half shows white paper");
        Assert.True(await doc.SetAnnotationHiddenAsync(0, added.Name, true));
    }

    [Fact]
    public async Task Turning_a_signature_keeps_it_centred()
    {
        string path = _files.Create(1, "Hello");
        await using var doc = await Engine.OpenAsync(path);
        var pixels = Enumerable.Repeat((byte)60, 40 * 40 * 4).ToArray();

        var added = await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(
            PdfAnnotationKind.Signature,
            new PdfColor(0, 0, 0),
            [new NormalizedRect(0.4, 0.4, 0.2, 0.1)],
            Picture: new PdfBitmap(40, 40, 160, pixels),
            RotationDegrees: 45));

        Assert.InRange(added.Bounds.X + (added.Bounds.Width / 2), 0.48, 0.52);
        Assert.InRange(added.Bounds.Y + (added.Bounds.Height / 2), 0.43, 0.47);
    }
}
