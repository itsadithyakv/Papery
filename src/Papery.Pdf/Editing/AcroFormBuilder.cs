using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SharpDoc = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Editing;

/// <summary>
/// Writes AcroForm fields as raw PDF dictionaries (PDF spec section 12.7).
/// PDFsharp can draw pages but has no API for creating fields, so we build them by hand.
/// Rectangles are given in top-left page coordinates like everything else in this tool.
/// </summary>
public sealed class AcroFormBuilder
{
    // Field flag bits (PDF spec tables 226, 228, 230).
    private const int FlagMultiline = 1 << 12;
    private const int FlagNoToggleToOff = 1 << 14;
    private const int FlagRadio = 1 << 15;
    private const int FlagCombo = 1 << 17;

    private readonly SharpDoc _doc;
    private readonly PdfArray _fields;

    public AcroFormBuilder(SharpDoc doc)
    {
        _doc = doc;

        // A document that already has a form keeps it; new fields join the same list.
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } existing)
        {
            _fields = existing.Elements.GetArray("/Fields") ?? new PdfArray(doc);
            existing.Elements["/Fields"] = _fields;
            existing.Elements.SetBoolean("/NeedAppearances", true);
            return;
        }

        _fields = new PdfArray(doc);

        var helvetica = AddIndirect(new PdfDictionary(doc));
        helvetica.Elements.SetName("/Type", "/Font");
        helvetica.Elements.SetName("/Subtype", "/Type1");
        helvetica.Elements.SetName("/BaseFont", "/Helvetica");
        helvetica.Elements.SetName("/Encoding", "/WinAnsiEncoding");

        var fonts = new PdfDictionary(doc);
        fonts.Elements.SetReference("/Helv", helvetica);
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;

        var acroForm = AddIndirect(new PdfDictionary(doc));
        acroForm.Elements["/Fields"] = _fields;
        acroForm.Elements["/DR"] = resources;
        acroForm.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        acroForm.Elements.SetBoolean("/NeedAppearances", true);
        doc.Internals.Catalog.Elements.SetReference("/AcroForm", acroForm);
    }

    public void AddText(PdfPage page, string name, XRect rect, bool multiline = false)
    {
        var field = NewWidget(page, rect, name);
        field.Elements.SetName("/FT", "/Tx");
        field.Elements.SetString("/DA", multiline ? "/Helv 10 Tf 0 g" : "/Helv 11 Tf 0 g");
        if (multiline)
        {
            field.Elements.SetInteger("/Ff", FlagMultiline);
        }

        Register(page, field);
    }

    public void AddCombo(PdfPage page, string name, XRect rect, string[] options, string selected)
    {
        var field = NewWidget(page, rect, name);
        field.Elements.SetName("/FT", "/Ch");
        field.Elements.SetInteger("/Ff", FlagCombo);
        field.Elements.SetString("/DA", "/Helv 11 Tf 0 g");
        field.Elements["/Opt"] = new PdfArray(_doc, options.Select(o => (PdfItem)new PdfString(o)).ToArray());
        field.Elements.SetString("/V", selected);
        Register(page, field);
    }

    public void AddCheckBox(PdfPage page, string name, XRect rect, bool isChecked)
    {
        var field = NewWidget(page, rect, name);
        field.Elements.SetName("/FT", "/Btn");
        string state = isChecked ? "/Yes" : "/Off";
        field.Elements.SetName("/V", state);
        field.Elements.SetName("/AS", state);
        field.Elements["/AP"] = Appearance(rect, "/Yes", CheckMark(rect), Box(rect));
        Register(page, field);
    }

    public void AddRadioGroup(PdfPage page, string name, string[] options, Func<int, XRect> rectFor, string selected)
    {
        // A radio group is one parent field (holds the value) with one widget per option.
        var parent = AddIndirect(new PdfDictionary(_doc));
        parent.Elements.SetName("/FT", "/Btn");
        parent.Elements.SetString("/T", name);
        parent.Elements.SetInteger("/Ff", FlagRadio | FlagNoToggleToOff);
        parent.Elements.SetName("/V", "/" + selected);
        var kids = new PdfArray(_doc);
        parent.Elements["/Kids"] = kids;

        for (int i = 0; i < options.Length; i++)
        {
            var rect = rectFor(i);
            var widget = NewWidget(page, rect, name: null);
            widget.Elements.SetReference("/Parent", parent);
            widget.Elements.SetName("/AS", options[i] == selected ? "/" + options[i] : "/Off");
            widget.Elements["/AP"] = Appearance(rect, "/" + options[i], Circle(rect, filled: true), Circle(rect, filled: false));
            kids.Elements.Add(widget.ReferenceNotNull);
            page.Annotations.Elements.Add(widget.ReferenceNotNull);
        }

        _fields.Elements.Add(parent.ReferenceNotNull);
    }

    public void AddSignature(PdfPage page, string name, XRect rect)
    {
        var field = NewWidget(page, rect, name);
        field.Elements.SetName("/FT", "/Sig");
        Register(page, field);
    }

    /// <summary>When on, rectangles are PDF user space (X, Y = bottom-left corner), not top-left page coordinates.</summary>
    public bool RectsInPdfSpace { get; set; }

    private PdfDictionary NewWidget(PdfPage page, XRect rect, string? name)
    {
        double h = page.Height.Point;
        var widget = AddIndirect(new PdfDictionary(_doc));
        widget.Elements.SetName("/Type", "/Annot");
        widget.Elements.SetName("/Subtype", "/Widget");
        widget.Elements.SetInteger("/F", 4); // print
        widget.Elements["/Rect"] = RectsInPdfSpace
            ? new PdfRectangle(new XPoint(rect.Left, rect.Top), new XPoint(rect.Right, rect.Bottom))
            : new PdfRectangle(new XPoint(rect.Left, h - rect.Bottom), new XPoint(rect.Right, h - rect.Top));
        widget.Elements.SetReference("/P", page);
        if (name != null)
        {
            widget.Elements.SetString("/T", name);
        }

        var mk = new PdfDictionary(_doc);
        mk.Elements["/BC"] = new PdfArray(_doc, new PdfReal(0.55), new PdfReal(0.55), new PdfReal(0.55));
        mk.Elements["/BG"] = new PdfArray(_doc, new PdfReal(0.97), new PdfReal(0.97), new PdfReal(1));
        widget.Elements["/MK"] = mk;
        return widget;
    }

    private void Register(PdfPage page, PdfDictionary field)
    {
        _fields.Elements.Add(field.ReferenceNotNull);
        page.Annotations.Elements.Add(field.ReferenceNotNull);
    }

    /// <summary>/AP &lt;&lt; /N &lt;&lt; /OnName on /Off off &gt;&gt; &gt;&gt;: how the button looks in each state.</summary>
    private PdfDictionary Appearance(XRect rect, string onName, string onContent, string offContent)
    {
        var normal = new PdfDictionary(_doc);
        normal.Elements.SetReference(onName, FormXObject(rect, onContent));
        normal.Elements.SetReference("/Off", FormXObject(rect, offContent));
        var ap = new PdfDictionary(_doc);
        ap.Elements["/N"] = normal;
        return ap;
    }

    private PdfDictionary FormXObject(XRect rect, string content)
    {
        var xobject = AddIndirect(new PdfDictionary(_doc));
        xobject.Elements.SetName("/Type", "/XObject");
        xobject.Elements.SetName("/Subtype", "/Form");
        xobject.Elements["/BBox"] = new PdfRectangle(new XPoint(0, 0), new XPoint(rect.Width, rect.Height));
        xobject.CreateStream(Encoding.ASCII.GetBytes(content));
        return xobject;
    }

    private static string Box(XRect r) =>
        FormattableString.Invariant($"0.97 0.97 1 rg 0.55 G 0.75 w 0.5 0.5 {r.Width - 1:0.##} {r.Height - 1:0.##} re B");

    private static string CheckMark(XRect r) =>
        Box(r) + FormattableString.Invariant($" 0 G 1.6 w 1 J 1 j {r.Width * 0.22:0.##} {r.Height * 0.52:0.##} m {r.Width * 0.42:0.##} {r.Height * 0.26:0.##} l {r.Width * 0.8:0.##} {r.Height * 0.78:0.##} l S");

    private static string Circle(XRect r, bool filled)
    {
        // Four Bézier curves approximate a circle; k is the standard control-point factor.
        static string Path(double cx, double cy, double rad)
        {
            const double k = 0.5523;
            double o = rad * k;
            return FormattableString.Invariant($"{cx + rad:0.##} {cy:0.##} m {cx + rad:0.##} {cy + o:0.##} {cx + o:0.##} {cy + rad:0.##} {cx:0.##} {cy + rad:0.##} c ") +
                   FormattableString.Invariant($"{cx - o:0.##} {cy + rad:0.##} {cx - rad:0.##} {cy + o:0.##} {cx - rad:0.##} {cy:0.##} c ") +
                   FormattableString.Invariant($"{cx - rad:0.##} {cy - o:0.##} {cx - o:0.##} {cy - rad:0.##} {cx:0.##} {cy - rad:0.##} c ") +
                   FormattableString.Invariant($"{cx + o:0.##} {cy - rad:0.##} {cx + rad:0.##} {cy - o:0.##} {cx + rad:0.##} {cy:0.##} c");
        }

        double c = r.Width / 2;
        string ring = "0.97 0.97 1 rg 0.55 G 0.75 w " + Path(c, c, c - 0.5) + " B";
        return filled ? ring + " 0 g " + Path(c, c, c * 0.45) + " f" : ring;
    }

    private PdfDictionary AddIndirect(PdfDictionary dict)
    {
        _doc.Internals.AddObject(dict);
        return dict;
    }
}
