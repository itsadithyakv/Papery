using System.Text;
using System.Xml.Linq;
using Papery.Pdf.Editing;
using PdfSharp.Drawing;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Tests;

/// <summary>Swapping comments (XFDF) and form answers (CSV, XFDF) with other apps.</summary>
public sealed class PdfExchangeTests : IDisposable
{
    private static readonly PdfColor Red = new(220, 30, 30);
    private static readonly PdfColor SeeThroughYellow = new(255, 220, 0, 110);
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    private readonly TestPdf _files = new();
    private static PdfEngine Engine => PdfEngine.Shared;

    public void Dispose() => _files.Dispose();

    private string Out(string name) => Path.Combine(_files.Folder, name);

    private static IReadOnlyList<IReadOnlyList<PagePoint>> Stroke(params (double X, double Y)[] points) =>
        [points.Select(p => new PagePoint(p.X, p.Y)).ToArray()];

    /// <summary>One of each kind of comment Papery makes, spread over two pages.</summary>
    private static List<(int Page, PdfAnnotationDraft Draft)> EveryKind()
    {
        var box = new NormalizedRect(0.3, 0.4, 0.3, 0.1);
        return
        [
            (0, new(PdfAnnotationKind.Highlight, SeeThroughYellow, [box, new NormalizedRect(0.3, 0.52, 0.2, 0.03)], Text: "Check this", Author: "Sam")),
            (0, new(PdfAnnotationKind.Underline, Red, [new NormalizedRect(0.1, 0.1, 0.3, 0.03)])),
            (0, new(PdfAnnotationKind.Squiggly, Red, [new NormalizedRect(0.1, 0.15, 0.3, 0.03)])),
            (0, new(PdfAnnotationKind.StrikeOut, Red, [new NormalizedRect(0.1, 0.2, 0.3, 0.03)])),
            (0, new(PdfAnnotationKind.Note, Red, [new NormalizedRect(0.8, 0.1, 0.01, 0.01)], Text: "A note, with \"quotes\" & <brackets>", Author: "Adi")),
            (0, new(PdfAnnotationKind.TextBox, Red, [new NormalizedRect(0.1, 0.6, 0.4, 0.1)], Text: "Typed words\nover two lines", FontSize: 16)),
            (0, new(PdfAnnotationKind.Callout, Red, [new NormalizedRect(0.55, 0.6, 0.3, 0.08)], Stroke((0.55, 0.64), (0.4, 0.8)), Text: "Look here", FontSize: 14)),
            (0, new(PdfAnnotationKind.Stamp, Red, [new NormalizedRect(0.6, 0.85, 0.3, 0.08)], Text: "APPROVED")),
            (0, new(PdfAnnotationKind.Whiteout, new PdfColor(255, 255, 255), [new NormalizedRect(0.05, 0.9, 0.1, 0.05)])),
            (1, new(PdfAnnotationKind.Rectangle, Red, [box], Width: 3)),
            (1, new(PdfAnnotationKind.Ellipse, Red, [new NormalizedRect(0.1, 0.1, 0.2, 0.1)], Width: 2)),
            (1, new(PdfAnnotationKind.Cloud, Red, [new NormalizedRect(0.6, 0.1, 0.2, 0.1)], Width: 2)),
            (1, new(PdfAnnotationKind.Line, Red, Strokes: Stroke((0.1, 0.6), (0.4, 0.65)), Width: 2)),
            (1, new(PdfAnnotationKind.Arrow, Red, Strokes: Stroke((0.1, 0.7), (0.4, 0.75)), Width: 2)),
            (1, new(PdfAnnotationKind.Measure, Red, Strokes: Stroke((0.5, 0.7), (0.8, 0.7)), Width: 2, Text: "12.3 cm")),
            (1, new(PdfAnnotationKind.Polygon, Red, Strokes: Stroke((0.5, 0.8), (0.8, 0.8), (0.65, 0.9)), Width: 2)),
            (1, new(PdfAnnotationKind.Pen, Red, Strokes: [.. Stroke((0.1, 0.8), (0.2, 0.85), (0.3, 0.8)), .. Stroke((0.1, 0.9), (0.3, 0.92))], Width: 3)),
            (1, new(PdfAnnotationKind.Marker, SeeThroughYellow, Strokes: Stroke((0.1, 0.95), (0.4, 0.95)), Width: 10)),
        ];
    }

    private async Task<string> Annotated(IEnumerable<(int Page, PdfAnnotationDraft Draft)> drafts, string? password = null, string name = "annotated.pdf")
    {
        string path = _files.Create(2, "Review", password, name);
        await using var doc = await Engine.OpenAsync(path, password);
        foreach (var (page, draft) in drafts)
        {
            await doc.AddAnnotationAsync(page, draft);
        }

        await doc.SaveAsync(path);
        return path;
    }

    private static async Task<List<PdfAnnotationInfo>> AllAnnotations(PdfDocument doc)
    {
        var all = new List<PdfAnnotationInfo>();
        for (int page = 0; page < doc.PageCount; page++)
        {
            all.AddRange(await doc.GetAnnotationsAsync(page));
        }

        return all;
    }

    private static void Near(NormalizedRect expected, NormalizedRect actual, string what)
    {
        const double Close = 0.004;
        Assert.True(
            Math.Abs(expected.X - actual.X) < Close && Math.Abs(expected.Y - actual.Y) < Close
            && Math.Abs(expected.Width - actual.Width) < Close && Math.Abs(expected.Height - actual.Height) < Close,
            $"{what}: expected {expected}, got {actual}");
    }

    // ================================================================= comments

    [Fact]
    public async Task Every_kind_of_comment_goes_out_and_comes_back_in_the_same_place()
    {
        string source = await Annotated(EveryKind());
        string xfdf = Out("comments.xfdf");

        int exported = await Engine.ExportCommentsAsync(new PdfInputFile(source), xfdf);

        Assert.Equal(EveryKind().Count - 1, exported); // the whiteout covers; it isn't a comment
        var root = XDocument.Load(xfdf).Root!;
        Assert.Equal(Ns + "xfdf", root.Name);
        var elements = root.Element(Ns + "annots")!.Elements().Select(e => e.Name.LocalName).ToHashSet();
        foreach (string element in new[] { "highlight", "underline", "squiggly", "strikeout", "text", "freetext", "stamp", "square", "circle", "line", "polygon", "ink" })
        {
            Assert.Contains(element, elements);
        }

        Assert.Contains("coords", root.Descendants(Ns + "highlight").Single().Attributes().Select(a => a.Name.LocalName));
        Assert.Equal("annotated.pdf", root.Element(Ns + "f")!.Attribute("href")!.Value);

        string clean = _files.Create(2, "Review", name: "clean.pdf");
        string output = Out("imported.pdf");
        var result = await Engine.ImportCommentsAsync(new PdfInputFile(clean), xfdf, output);

        Assert.Equal(new CommentImportResult(exported, 0, 0), result);
        await using var original = await Engine.OpenAsync(source);
        await using var imported = await Engine.OpenAsync(output);
        var before = (await AllAnnotations(original)).Where(a => a.Kind != PdfAnnotationKind.Whiteout).ToList();
        var after = await AllAnnotations(imported);
        Assert.Equal(before.Count, after.Count);
        foreach (var a in before)
        {
            var b = Assert.Single(after, x => x.Name == a.Name);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.PageIndex, b.PageIndex);
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(a.Author, b.Author);
            Assert.Equal(a.Color, b.Color);
            Near(a.Bounds, b.Bounds, a.Kind.ToString());
        }

        // Imported comments are Papery's own again, so they stay editable.
        var callout = after.Single(a => a.Kind == PdfAnnotationKind.Callout);
        var draft = await imported.GetAnnotationDraftAsync(callout.PageIndex, callout.Name);
        Assert.NotNull(draft);
        Assert.Equal(14, draft.FontSize);
        Assert.Equal(2, draft.Strokes![0].Count);
    }

    [Fact]
    public async Task Reading_the_same_comments_twice_does_not_double_them()
    {
        string source = await Annotated([(0, new PdfAnnotationDraft(PdfAnnotationKind.Note, Red, [new NormalizedRect(0.5, 0.5, 0, 0)], Text: "Once"))]);
        string xfdf = Out("once.xfdf");
        await Engine.ExportCommentsAsync(new PdfInputFile(source), xfdf);

        var again = await Engine.ImportCommentsAsync(new PdfInputFile(source), xfdf, Out("again.pdf"));

        Assert.Equal(new CommentImportResult(0, 0, 1), again);
        Assert.False(File.Exists(Out("again.pdf")), "with nothing added, nothing is written");
    }

    [Fact]
    public async Task Comments_land_in_the_right_place_on_a_turned_page()
    {
        string path = Out("turned.pdf");
        var sharp = new SharpDocument();
        foreach (int rotate in new[] { 90, 270 })
        {
            var page = sharp.AddPage();
            page.Width = XUnit.FromPoint(595);
            page.Height = XUnit.FromPoint(842);
            page.Rotate = rotate;
        }

        sharp.Save(path);
        string clean = Out("turned-clean.pdf");
        File.Copy(path, clean);
        var mark = new NormalizedRect(0.1, 0.2, 0.3, 0.05);
        await using (var doc = await Engine.OpenAsync(path))
        {
            await doc.AddAnnotationAsync(0, new PdfAnnotationDraft(PdfAnnotationKind.Highlight, SeeThroughYellow, [mark]));
            await doc.AddAnnotationAsync(1, new PdfAnnotationDraft(PdfAnnotationKind.TextBox, Red, [mark], Text: "Sideways"));
            await doc.SaveAsync(path);
        }

        string xfdf = Out("turned.xfdf");
        Assert.Equal(2, await Engine.ExportCommentsAsync(new PdfInputFile(path), xfdf));
        await Engine.ImportCommentsAsync(new PdfInputFile(clean), xfdf, clean);

        await using var imported = await Engine.OpenAsync(clean);
        Near(mark, Assert.Single(await imported.GetAnnotationsAsync(0)).Bounds, "highlight");
        var box = Assert.Single(await imported.GetAnnotationsAsync(1));
        Assert.Equal("Sideways", box.Text);
        Assert.InRange(box.Bounds.X, 0.08, 0.12);
        Assert.InRange(box.Bounds.Y, 0.18, 0.22);
    }

    [Fact]
    public async Task An_Acrobat_comments_file_is_read()
    {
        const string Acrobat = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
            <annots>
            <text color="#FFFF00" creationdate="D:20260101090000+05'30'" date="D:20260102120000+05'30'" flags="print,nozoom,norotate" icon="Comment" name="5f3c2a10-1111-4c1e-9d21-0a0b0c0d0e0f" page="0" rect="100,700,120,720" subject="Sticky Note" title="Reviewer"><contents-richtext><body xmlns="http://www.w3.org/1999/xhtml" xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/" xfa:APIVersion="Acrobat:23.1.0" xfa:spec="2.0.2"><p>Please check this figure.</p></body></contents-richtext><popup flags="print,nozoom,norotate" open="no" page="0" rect="120,620,300,720"/></text>
            <highlight color="#FFFF00" coords="72,760,200,760,72,745,200,745" date="D:20260102120000+05'30'" flags="print" name="hl-1" opacity="1" page="0" rect="71,744,201,761" subject="Highlight" title="Reviewer"><contents>Key point</contents></highlight>
            <square color="#FF0000" width="2" flags="print" name="sq-1" page="0" rect="300,500,400,600" subject="Rectangle" title="Reviewer"/>
            <line color="#0000FF" start="100,300" end="300,350" head="None" tail="OpenArrow" width="1" flags="print" name="ln-1" page="0" rect="95,295,305,355" subject="Arrow" title="Reviewer"/>
            <ink color="#00AA00" width="2" flags="print" name="ink-1" page="0" rect="98,98,202,152" subject="Pencil" title="Reviewer"><inklist><gesture>100,100;150,150;200,120</gesture><gesture>100,140;200,140</gesture></inklist></ink>
            <freetext flags="print" name="ft-1" page="0" rect="300,300,500,350" subject="Text Box" title="Reviewer"><contents>Typed words</contents><defaultappearance>0 0 1 rg /Helv 14 Tf</defaultappearance><defaultstyle>font: Helvetica 14pt; color:#0000FF</defaultstyle></freetext>
            <stamp color="#FF0000" flags="print" icon="Approved" name="st-1" page="0" rect="350,650,500,700" subject="Approved" title="Reviewer"/>
            <caret color="#0000FF" flags="print" name="ca-1" page="0" rect="50,50,60,60" title="Reviewer"/>
            <text flags="print,nozoom,norotate,hidden" icon="Comment" inreplyto="5f3c2a10-1111-4c1e-9d21-0a0b0c0d0e0f" name="rv-1" page="0" rect="100,700,120,720" state="Accepted" statemodel="Review" title="Reviewer"/>
            <fileattachment flags="print" icon="PushPin" name="fa-1" page="0" rect="20,20,30,30"/>
            <text name="far-1" page="7" rect="100,700,120,720"><contents>On a page this PDF hasn't got</contents></text>
            <somethingnew page="0" rect="1,1,2,2"/>
            </annots>
            <f href="Document.pdf"/>
            <ids original="0123456789ABCDEF" modified="FEDCBA9876543210"/>
            </xfdf>
            """;
        string xfdf = Out("acrobat.xfdf");
        await File.WriteAllTextAsync(xfdf, Acrobat);
        string path = _files.Create(1, "Hello");

        var result = await Engine.ImportCommentsAsync(new PdfInputFile(path), xfdf, path);

        Assert.Equal(7, result.Added);
        Assert.Equal(5, result.Skipped); // caret, review tick, attachment, missing page, unknown element
        await using var doc = await Engine.OpenAsync(path);
        var all = await doc.GetAnnotationsAsync(0);
        Assert.Equal(
            new[] { PdfAnnotationKind.Note, PdfAnnotationKind.Highlight, PdfAnnotationKind.Rectangle, PdfAnnotationKind.Arrow, PdfAnnotationKind.Pen, PdfAnnotationKind.TextBox, PdfAnnotationKind.Stamp }.Order(),
            all.Select(a => a.Kind).Order());
        var note = all.Single(a => a.Kind == PdfAnnotationKind.Note);
        Assert.Equal("Please check this figure.", note.Text);
        Assert.Equal("Reviewer", note.Author);
        Assert.Equal("5f3c2a10-1111-4c1e-9d21-0a0b0c0d0e0f", note.Name);
        Assert.Equal("Key point", all.Single(a => a.Kind == PdfAnnotationKind.Highlight).Text);
        Assert.Equal(110, all.Single(a => a.Kind == PdfAnnotationKind.Highlight).Color.A); // see-through, so the words show
        Assert.Equal("Approved", all.Single(a => a.Kind == PdfAnnotationKind.Stamp).Text);
        var box = all.Single(a => a.Kind == PdfAnnotationKind.TextBox);
        Assert.Equal(new PdfColor(0, 0, 255), box.Color);
        Assert.Equal(14, (await doc.GetAnnotationDraftAsync(0, box.Name))!.FontSize);
        Assert.Equal(2, (await doc.GetAnnotationDraftAsync(0, "ink-1"))!.Strokes!.Count);

        // The square's outline sits inside its rectangle: 300-400 with a 2 pt line is drawn from 301 to 399.
        var square = (await doc.GetAnnotationDraftAsync(0, "sq-1"))!.Areas![0];
        Assert.InRange(square.X * 595.28, 300.5, 301.5);
        Assert.InRange(square.Right * 595.28, 398.5, 399.5);

        // The reviewer's dates are kept, not replaced by the moment of importing.
        string back = Out("back.xfdf");
        await Engine.ExportCommentsAsync(doc, back);
        var exportedNote = XDocument.Load(back).Descendants(Ns + "text").Single();
        Assert.Equal("D:20260102120000+05'30'", exportedNote.Attribute("date")!.Value);
        Assert.Equal("D:20260101090000+05'30'", exportedNote.Attribute("creationdate")!.Value);
    }

    [Fact]
    public async Task A_hostile_comments_file_is_refused_or_ignored_safely()
    {
        string path = _files.Create(1, "Hello");
        string output = Out("out.pdf");

        // Entity expansion ("billion laughs") needs a DTD, and a DTD is refused outright.
        string laughs = Out("laughs.xfdf");
        await File.WriteAllTextAsync(laughs, """
            <?xml version="1.0"?>
            <!DOCTYPE xfdf [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <xfdf xmlns="http://ns.adobe.com/xfdf/"><annots><text page="0" rect="1,1,20,20"><contents>&c;</contents></text></annots></xfdf>
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine.ImportCommentsAsync(new PdfInputFile(path), laughs, output));

        string external = Out("external.xfdf");
        await File.WriteAllTextAsync(external, """
            <?xml version="1.0"?>
            <!DOCTYPE xfdf SYSTEM "file:///C:/Windows/win.ini">
            <xfdf xmlns="http://ns.adobe.com/xfdf/"><annots/></xfdf>
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine.ImportCommentsAsync(new PdfInputFile(path), external, output));

        string notXml = Out("not.xfdf");
        await File.WriteAllTextAsync(notXml, "this is not xml at all");
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine.ImportCommentsAsync(new PdfInputFile(path), notXml, output));
        Assert.False(File.Exists(output));

        // Huge counts and silly numbers: each bad comment is skipped, and past the cap the rest aren't even read.
        var huge = new StringBuilder("""<xfdf xmlns="http://ns.adobe.com/xfdf/"><annots>""");
        huge.Append("""<text page="0" rect="10,10,30,30"><contents>""").Append('x', 300_000).Append("</contents></text>");
        huge.Append("""<ink page="0" rect="0,0,1,1"><inklist><gesture>""");
        huge.AppendJoin(';', Enumerable.Range(0, 50_000).Select(i => $"{i % 500},{i % 700}"));
        huge.Append("</gesture></inklist></ink>");
        huge.Append("""<text page="99999999999" rect="1,1,2,2"/><text page="0" rect="NaN,1,2,2"/><text page="0" rect="1e300,1,2,2"/><text page="-1" rect="1,1,2,2"/>""");
        for (int i = 0; i < CommentExchange.MostComments + 50; i++)
        {
            huge.Append("""<text page="5" rect="1,1,2,2"/>""");
        }

        huge.Append("</annots></xfdf>");
        string hugePath = Out("huge.xfdf");
        await File.WriteAllTextAsync(hugePath, huge.ToString());

        var result = await Engine.ImportCommentsAsync(new PdfInputFile(path), hugePath, output);

        Assert.Equal(1, result.Added);
        Assert.Equal(1 + 4 + 51 + (CommentExchange.MostComments - 1), result.Skipped); // the ink, the bad numbers, past the cap, the missing page
        await using var doc = await Engine.OpenAsync(output);
        var note = Assert.Single(await doc.GetAnnotationsAsync(0));
        Assert.Equal(Xfdf.LongestText, note.Text!.Length);
    }

    [Fact]
    public async Task A_locked_file_stays_locked_after_reading_comments_in()
    {
        string source = await Annotated([(0, new PdfAnnotationDraft(PdfAnnotationKind.Highlight, SeeThroughYellow, [new NormalizedRect(0.2, 0.2, 0.3, 0.03)], Text: "Secret note"))], password: "pw");
        string xfdf = Out("locked.xfdf");
        Assert.Equal(1, await Engine.ExportCommentsAsync(new PdfInputFile(source, "pw"), xfdf));

        string target = _files.Create(2, "Other", password: "pw2", name: "target.pdf");
        var result = await Engine.ImportCommentsAsync(new PdfInputFile(target, "pw2"), xfdf, target);

        Assert.Equal(1, result.Added);
        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(target));
        Assert.Equal(PdfOpenError.PasswordRequired, ex.Error);
        await using var doc = await Engine.OpenAsync(target, "pw2");
        Assert.Equal("Secret note", Assert.Single(await doc.GetAnnotationsAsync(0)).Text);
    }

    // ================================================================= form answers

    /// <summary>The repo's interactive test form when it's there; otherwise one built the same way.</summary>
    private string InteractiveForm(string? password = null)
    {
        string copy = Out($"form-{Guid.NewGuid():N}.pdf");
        for (string? folder = AppContext.BaseDirectory; folder != null && password == null; folder = Path.GetDirectoryName(folder))
        {
            string candidate = Path.Combine(folder, "PaperyTestFiles", "forms-interactive.pdf");
            if (File.Exists(candidate))
            {
                File.Copy(candidate, copy);
                return copy;
            }
        }

        var doc = new SharpDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        var form = new AcroFormBuilder(doc);
        form.AddText(page, "full_name", new XRect(72, 100, 300, 22));
        form.AddText(page, "email", new XRect(72, 140, 300, 22));
        form.AddText(page, "phone", new XRect(72, 180, 200, 22));
        form.AddText(page, "date_of_birth", new XRect(72, 220, 140, 22));
        form.AddCombo(page, "country", new XRect(72, 260, 220, 22), ["India", "United Kingdom", "United States", "Japan"], "United Kingdom");
        form.AddText(page, "address", new XRect(72, 300, 300, 66), multiline: true);
        form.AddRadioGroup(page, "membership", ["Basic", "Standard", "Premium"], i => new XRect(76 + (i * 120), 400, 14, 14), selected: "Standard");
        form.AddCheckBox(page, "newsletter", new XRect(72, 440, 14, 14), isChecked: true);
        form.AddCheckBox(page, "accept_terms", new XRect(72, 470, 14, 14), isChecked: false);
        form.AddSignature(page, "signature", new XRect(72, 520, 260, 60));
        form.AddText(page, "signed_date", new XRect(372, 520, 140, 22));
        if (password != null)
        {
            doc.SecurityHandler.SetEncryptionToV5();
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password + "-owner";
        }

        doc.Save(copy);
        return copy;
    }

    private static async Task<Dictionary<string, List<PdfFormField>>> Fields(PdfDocument doc)
    {
        var all = new List<PdfFormField>();
        for (int page = 0; page < doc.PageCount; page++)
        {
            all.AddRange(await doc.GetFormFieldsAsync(page));
        }

        return all.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Fills the form the way a person would, with answers that are awkward for CSV and Excel.</summary>
    private static async Task Fill(string path, string? password = null)
    {
        await using var doc = await Engine.OpenAsync(path, password);
        var f = await Fields(doc);
        async Task Text(string name, string value) => await doc.SetFieldTextAsync(f[name][0].PageIndex, f[name][0].Index, value);
        await Text("full_name", "Vinod, Adi \"Jr\"");
        await Text("email", "adi@example.com");
        await Text("phone", "+44 20 7946 0000");
        await Text("date_of_birth", "=1+1");
        await Text("address", "12 Harbour Street\rKochi");
        await Text("signed_date", "03/11/2026");
        await doc.SetFieldOptionAsync(f["country"][0].PageIndex, f["country"][0].Index, f["country"][0].Options.ToList().IndexOf("Japan"));
        var premium = f["membership"][2];
        await doc.SetFieldCheckedAsync(premium.PageIndex, premium.Index, true);
        await doc.SetFieldCheckedAsync(f["newsletter"][0].PageIndex, f["newsletter"][0].Index, false);
        await doc.SetFieldCheckedAsync(f["accept_terms"][0].PageIndex, f["accept_terms"][0].Index, true);
        await doc.SaveAsync(path);
    }

    private static async Task<Dictionary<string, (string Value, bool[] Checked, int Option)>> Answers(string path, string? password = null)
    {
        await using var doc = await Engine.OpenAsync(path, password);
        return (await Fields(doc)).ToDictionary(p => p.Key, p => (p.Value[0].Value, p.Value.Select(w => w.IsChecked).ToArray(), p.Value[0].SelectedOption));
    }

    private static void SameAnswers(Dictionary<string, (string Value, bool[] Checked, int Option)> expected, Dictionary<string, (string Value, bool[] Checked, int Option)> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, want) in expected)
        {
            var got = actual[name];
            Assert.True(want.Value == got.Value, $"{name}: expected '{want.Value}', got '{got.Value}'");
            Assert.True(want.Checked.SequenceEqual(got.Checked), $"{name}: ticks differ");
            Assert.True(want.Option == got.Option, $"{name}: option {want.Option} vs {got.Option}");
        }
    }

    [Theory]
    [InlineData(FormDataFormat.Csv)]
    [InlineData(FormDataFormat.Xfdf)]
    public async Task Form_answers_go_out_and_fill_a_blank_copy(FormDataFormat format)
    {
        string blank = InteractiveForm();
        string filled = Out("filled.pdf");
        File.Copy(blank, filled);
        await Fill(filled);
        string data = Out(format == FormDataFormat.Csv ? "answers.csv" : "answers.xfdf");

        int exported = await Engine.ExportFormDataAsync(new PdfInputFile(filled), data, format);

        Assert.Equal(10, exported); // every field but the signature
        if (format == FormDataFormat.Csv)
        {
            byte[] bytes = await File.ReadAllBytesAsync(data);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // Excel needs the BOM to read UTF-8
            string csv = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.StartsWith("Field,Value\r\n", csv, StringComparison.Ordinal);
            Assert.Contains("\"full_name\",\"Vinod, Adi \"\"Jr\"\"\"", csv, StringComparison.Ordinal);
            Assert.Contains("\"date_of_birth\",\"'=1+1\"", csv, StringComparison.Ordinal); // never runs as a formula
            Assert.Contains("\"membership\",\"Premium\"", csv, StringComparison.Ordinal);
            Assert.Contains("\"newsletter\",\"Off\"", csv, StringComparison.Ordinal);
        }
        else
        {
            var fields = XDocument.Load(data).Root!.Element(Ns + "fields")!.Elements(Ns + "field").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element(Ns + "value")!.Value);
            Assert.Equal("Premium", fields["membership"]);
            Assert.Equal("Japan", fields["country"]);
            Assert.DoesNotContain("signature", fields.Keys);
        }

        string output = Out("refilled.pdf");
        var result = await Engine.ImportFormDataAsync(new PdfInputFile(blank), data, output);

        Assert.Equal(new FormImportResult(10, 0, 0), result);
        SameAnswers(await Answers(filled), await Answers(output));
    }

    [Fact]
    public async Task Acrobat_form_data_and_a_European_spreadsheet_are_read()
    {
        string blank = InteractiveForm();
        string xfdf = Out("acrobat-fields.xfdf");
        await File.WriteAllTextAsync(xfdf, """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
            <f href="forms-interactive.pdf"/>
            <fields>
            <field name="full_name"><value>Jo Bloggs</value></field>
            <field name="membership"><value>Basic</value></field>
            <field name="accept_terms"><value>Yes</value></field>
            <field name="newsletter"><value>Off</value></field>
            <field name="country"><value>Atlantis</value></field>
            <field name="nickname"><value>JB</value></field>
            <field name="signature"><value>scribble</value></field>
            <field name="person"><field name="age"><value>40</value></field></field>
            </fields>
            <ids original="0123" modified="4567"/>
            </xfdf>
            """);
        string output = Out("from-acrobat.pdf");

        var result = await Engine.ImportFormDataAsync(new PdfInputFile(blank), xfdf, output);

        Assert.Equal(new FormImportResult(4, 2, 2), result); // nickname and person.age unknown; Atlantis and the signature skipped
        await using (var doc = await Engine.OpenAsync(output))
        {
            var f = await Fields(doc);
            Assert.Equal("Jo Bloggs", f["full_name"][0].Value);
            Assert.True(f["membership"].Select(w => w.IsChecked).SequenceEqual([true, false, false]), "Basic is the one picked");
            Assert.True(f["accept_terms"][0].IsChecked);
            Assert.False(f["newsletter"][0].IsChecked);
        }

        // Excel in much of Europe saves with semicolons, and older versions in the Western code page.
        string csv = Out("europe.csv");
        await File.WriteAllBytesAsync(csv, Encoding.Latin1.GetBytes("Field name;Value\r\nfull_name;\"José Müller; Senior\"\r\ncountry;India\r\naccept_terms;yes\r\n\r\n"));
        var fromCsv = await Engine.ImportFormDataAsync(new PdfInputFile(blank), csv, output);

        Assert.Equal(new FormImportResult(3, 0, 0), fromCsv);
        await using var again = await Engine.OpenAsync(output);
        var g = await Fields(again);
        Assert.Equal("José Müller; Senior", g["full_name"][0].Value);
        Assert.Equal("India", g["country"][0].Options[g["country"][0].SelectedOption]);
        Assert.True(g["accept_terms"][0].IsChecked);
    }

    [Fact]
    public async Task Form_data_files_with_tricks_are_refused()
    {
        string blank = InteractiveForm();
        string output = Out("out.pdf");
        string dtd = Out("dtd.xfdf");
        await File.WriteAllTextAsync(dtd, """
            <?xml version="1.0"?>
            <!DOCTYPE xfdf [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <xfdf xmlns="http://ns.adobe.com/xfdf/"><fields><field name="full_name"><value>&b;</value></field></fields></xfdf>
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine.ImportFormDataAsync(new PdfInputFile(blank), dtd, output));

        // Deep nesting and endless rows don't run away: names stop nesting, and rows stop at the cap.
        var deep = new StringBuilder("""<xfdf xmlns="http://ns.adobe.com/xfdf/"><fields>""");
        deep.Insert(deep.Length, """<field name="n">""", 200);
        deep.Append("<value>x</value>");
        deep.Insert(deep.Length, "</field>", 200);
        deep.Append("</fields></xfdf>");
        string deepPath = Out("deep.xfdf");
        await File.WriteAllTextAsync(deepPath, deep.ToString());
        Assert.Equal(new FormImportResult(0, 0, 0), await Engine.ImportFormDataAsync(new PdfInputFile(blank), deepPath, output));

        string rows = Out("rows.csv");
        await File.WriteAllTextAsync(rows, string.Concat(Enumerable.Range(0, FormDataExchange.MostFields + 500).Select(i => $"field{i},value\r\n")));
        var result = await Engine.ImportFormDataAsync(new PdfInputFile(blank), rows, output);
        Assert.Equal(new FormImportResult(0, FormDataExchange.MostFields, 0), result);
        Assert.False(File.Exists(output), "nothing filled, so nothing written");
    }

    [Fact]
    public async Task A_locked_form_stays_locked_after_filling_from_a_file()
    {
        string source = InteractiveForm("pw");
        await Fill(source, "pw");
        string csv = Out("locked.csv");
        Assert.Equal(10, await Engine.ExportFormDataAsync(new PdfInputFile(source, "pw"), csv, FormDataFormat.Csv));

        string blank = InteractiveForm("pw");
        var result = await Engine.ImportFormDataAsync(new PdfInputFile(blank, "pw"), csv, blank);

        Assert.Equal(10, result.Filled);
        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => Engine.OpenAsync(blank));
        Assert.Equal(PdfOpenError.PasswordRequired, ex.Error);
        SameAnswers(await Answers(source, "pw"), await Answers(blank, "pw"));
    }
}
