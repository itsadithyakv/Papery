using Papery.Pdf.Editing;
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
            DrawFooter(g, page, "Papery test file · forms-interactive.pdf · page 1");
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
            DrawFooter(g, page2, "Papery test file · forms-interactive.pdf · page 2");
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

        // A small table: 4 columns × 5 rows.
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
        DrawFooter(g, page, "Papery test file · forms-flat.pdf");
        doc.Save(path);
    }
}
