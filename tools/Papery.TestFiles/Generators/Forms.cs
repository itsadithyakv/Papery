using System.Text;
using static Papery.TestFiles.Layout;

namespace Papery.TestFiles.Generators;

/// <summary>An interactive AcroForm PDF and a "flat" printed form with only lines and boxes.</summary>
internal static class Forms
{
    public static void InteractiveForm(string path)
    {
        var doc = new SharpDoc();
        doc.Info.Title = "Papery interactive form test";
        var form = new AcroFormBuilder(doc);

        // ---- Page 1: membership application ----
        var page = AddPage(doc, A4);
        double width = page.Width.Point - (2 * Margin);
        using (var g = XGraphics.FromPdfPage(page))
        {
            double y = DrawTitle(g, page, "Membership application", "Interactive form: press Tab to move between fields.");
            foreach (string label in new[] { "Full name", "Email", "Phone", "Date of birth (DD/MM/YYYY)", "Country" })
            {
                g.DrawString(label, LabelFont, Ink, Margin, y, XStringFormats.TopLeft);
                y += 44;
            }

            g.DrawString("Address", LabelFont, Ink, Margin, y, XStringFormats.TopLeft);
            y += 92;
            g.DrawString("Membership", LabelFont, Ink, Margin, y, XStringFormats.TopLeft);
            foreach (var (option, i) in new[] { "Basic", "Standard", "Premium" }.Select((o, i) => (o, i)))
            {
                g.DrawString(option, LabelFont, Ink, Margin + 24 + (i * 120), y + 20, XStringFormats.TopLeft);
            }

            y += 56;
            g.DrawString("Send me the newsletter", LabelFont, Ink, Margin + 24, y, XStringFormats.TopLeft);
            g.DrawString("I accept the terms", LabelFont, Ink, Margin + 24, y + 26, XStringFormats.TopLeft);
            DrawFooter(g, page, "Papery test file Â· forms-interactive.pdf Â· page 1");
        }

        double fy = 130; // field rows line up with the labels drawn above
        form.AddText(page, "full_name", new XRect(Margin, fy + 14, width, 22));
        form.AddText(page, "email", new XRect(Margin, fy + 58, width, 22));
        form.AddText(page, "phone", new XRect(Margin, fy + 102, width / 2, 22));
        form.AddText(page, "date_of_birth", new XRect(Margin, fy + 146, 140, 22));
        form.AddCombo(page, "country", new XRect(Margin, fy + 190, 220, 22),
            ["India", "United Kingdom", "United States", "Germany", "France", "Japan", "Other"], "United Kingdom");
        form.AddText(page, "address", new XRect(Margin, fy + 234, width, 66), multiline: true);
        form.AddRadioGroup(page, "membership", ["Basic", "Standard", "Premium"],
            i => new XRect(Margin + 4 + (i * 120), fy + 330, 14, 14), selected: "Standard");
        form.AddCheckBox(page, "newsletter", new XRect(Margin, fy + 366, 14, 14), isChecked: true);
        form.AddCheckBox(page, "accept_terms", new XRect(Margin, fy + 392, 14, 14), isChecked: false);

        // ---- Page 2: signature ----
        var page2 = AddPage(doc, A4);
        using (var g = XGraphics.FromPdfPage(page2))
        {
            double y = DrawTitle(g, page2, "Declaration", "Sign below.");
            y = DrawWrapped(g, "I confirm that the information I have given is true and complete. " + Content.Paragraphs[5],
                BodyFont, Ink, Margin, y, width);
            g.DrawString("Signature", LabelFont, Ink, Margin, y + 30, XStringFormats.TopLeft);
            g.DrawString("Date", LabelFont, Ink, Margin + 300, y + 30, XStringFormats.TopLeft);
            DrawFooter(g, page2, "Papery test file Â· forms-interactive.pdf Â· page 2");
        }

        form.AddSignature(page2, "signature", new XRect(Margin, 300, 260, 60));
        form.AddText(page2, "signed_date", new XRect(Margin + 300, 300, 140, 22));

        doc.Save(path);
    }

    /// <summary>A printed-style form with no fields at all: lines, boxes and a table to fill by hand.</summary>
    public static void FlatForm(string path)
    {
        var doc = new SharpDoc();
        doc.Info.Title = "Papery flat (non-interactive) form test";
        var page = AddPage(doc, A4);
        using var g = XGraphics.FromPdfPage(page);
        double width = page.Width.Point - (2 * Margin);
        double y = DrawTitle(g, page, "School trip consent form", "Printed form: no interactive fields.");

        foreach (string label in new[] { "Student name", "Class", "Parent / guardian", "Phone number", "Date" })
        {
            g.DrawString(label, LabelFont, Ink, Margin, y + 6, XStringFormats.TopLeft);
            g.DrawLine(Rule, Margin + 130, y + 20, Margin + width, y + 20);
            y += 36;
        }

        y += 10;
        foreach (string label in new[] { "My child may attend the trip", "My child has allergies (give details below)", "I can help on the day" })
        {
            g.DrawRectangle(Rule, Margin, y, 12, 12);
            g.DrawString(label, LabelFont, Ink, Margin + 22, y, XStringFormats.TopLeft);
            y += 24;
        }

        y += 14;
        g.DrawString("Medical details", LabelFont, Ink, Margin, y, XStringFormats.TopLeft);
        g.DrawRectangle(Rule, Margin, y + 18, width, 80);
        y += 120;

        // A small table: 4 columns Ã— 5 rows.
        string[] headers = ["Item", "Quantity", "Price", "Total"];
        double colWidth = width / headers.Length;
        for (int row = 0; row <= 5; row++)
        {
            g.DrawLine(Rule, Margin, y + (row * 24), Margin + width, y + (row * 24));
        }

        for (int col = 0; col <= headers.Length; col++)
        {
            g.DrawLine(Rule, Margin + (col * colWidth), y, Margin + (col * colWidth), y + 120);
        }

        for (int col = 0; col < headers.Length; col++)
        {
            g.DrawString(headers[col], LabelFont, Ink, Margin + (col * colWidth) + 6, y + 6, XStringFormats.TopLeft);
        }

        y += 150;
        g.DrawString("Signature", LabelFont, Ink, Margin, y, XStringFormats.TopLeft);
        g.DrawLine(Rule, Margin + 70, y + 14, Margin + 300, y + 14);
        DrawFooter(g, page, "Papery test file Â· forms-flat.pdf");
        doc.Save(path);
    }

    /// <summary>
    /// Writes AcroForm fields as raw PDF dictionaries (PDF spec section 12.7).
    /// PDFsharp can draw pages but has no API for creating fields, so we build them by hand.
    /// Rectangles are given in top-left page coordinates like everything else in this tool.
    /// </summary>
    private sealed class AcroFormBuilder
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

        private PdfDictionary NewWidget(PdfPage page, XRect rect, string? name)
        {
            double h = page.Height.Point;
            var widget = AddIndirect(new PdfDictionary(_doc));
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements.SetInteger("/F", 4); // print
            widget.Elements["/Rect"] = new PdfRectangle(new XPoint(rect.Left, h - rect.Bottom), new XPoint(rect.Right, h - rect.Top));
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
            Inv($"0.97 0.97 1 rg 0.55 G 0.75 w 0.5 0.5 {r.Width - 1:0.##} {r.Height - 1:0.##} re B");

        private static string CheckMark(XRect r) =>
            Box(r) + Inv($" 0 G 1.6 w 1 J 1 j {r.Width * 0.22:0.##} {r.Height * 0.52:0.##} m {r.Width * 0.42:0.##} {r.Height * 0.26:0.##} l {r.Width * 0.8:0.##} {r.Height * 0.78:0.##} l S");

        private static string Circle(XRect r, bool filled)
        {
            // Four BÃ©zier curves approximate a circle; k is the standard control-point factor.
            static string Path(double cx, double cy, double rad)
            {
                const double k = 0.5523;
                double o = rad * k;
                return Inv($"{cx + rad:0.##} {cy:0.##} m {cx + rad:0.##} {cy + o:0.##} {cx + o:0.##} {cy + rad:0.##} {cx:0.##} {cy + rad:0.##} c ") +
                       Inv($"{cx - o:0.##} {cy + rad:0.##} {cx - rad:0.##} {cy + o:0.##} {cx - rad:0.##} {cy:0.##} c ") +
                       Inv($"{cx - rad:0.##} {cy - o:0.##} {cx - o:0.##} {cy - rad:0.##} {cx:0.##} {cy - rad:0.##} c ") +
                       Inv($"{cx + o:0.##} {cy - rad:0.##} {cx + rad:0.##} {cy - o:0.##} {cx + rad:0.##} {cy:0.##} c");
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
}
