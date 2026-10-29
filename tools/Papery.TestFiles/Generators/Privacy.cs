using System.Text;
using PdfSharp.Pdf.Annotations;
using static Papery.TestFiles.Layout;

namespace Papery.TestFiles.Generators;

/// <summary>Files for the Security stage: hidden data to strip, and personal data to redact.</summary>
internal static class Privacy
{
    /// <summary>
    /// One page carrying every kind of hidden data Papery's "Remove hidden data" must find:
    /// metadata, comments, an attached file, JavaScript, and text on a hidden layer.
    /// </summary>
    public static void HiddenData(string path)
    {
        var doc = new SharpDoc();
        doc.Info.Title = "Quarterly figures (CONFIDENTIAL DRAFT)";
        doc.Info.Author = "Jane Example";
        doc.Info.Subject = "Internal planning";
        doc.Info.Keywords = "internal, draft, do-not-share";
        doc.Info.Creator = "Papery test generator";

        var page = AddPage(doc, A4);
        using (var g = XGraphics.FromPdfPage(page))
        {
            double y = DrawTitle(g, page, "Quarterly figures", "This file carries hidden data on purpose.");
            y = DrawWrapped(g, "Visible text. This page has two comments, an attached file, a JavaScript action, " +
                "author metadata, and a line of text on a hidden layer that viewers do not show by default.",
                BodyFont, Ink, Margin, y, page.Width.Point - (2 * Margin));

            AddNote(page, g, new XRect(page.Width.Point - 90, 120, 20, 20), "Jane Example", "Please double-check these numbers before sending.");
            AddNote(page, g, new XRect(page.Width.Point - 90, 170, 20, 20), "Sam Reviewer", "Remove the draft figures before this goes out.");
        }

        // Text on a hidden layer (optional content group switched off by default).
        var ocg = AddIndirect(doc, new PdfDictionary(doc));
        ocg.Elements.SetName("/Type", "/OCG");
        ocg.Elements.SetString("/Name", "Hidden layer");
        var ocDefaults = new PdfDictionary(doc);
        ocDefaults.Elements.SetString("/Name", "Default");
        ocDefaults.Elements["/Order"] = new PdfArray(doc, ocg.ReferenceNotNull);
        ocDefaults.Elements["/OFF"] = new PdfArray(doc, ocg.ReferenceNotNull);
        var ocProperties = new PdfDictionary(doc);
        ocProperties.Elements["/OCGs"] = new PdfArray(doc, ocg.ReferenceNotNull);
        ocProperties.Elements["/D"] = ocDefaults;
        doc.Internals.Catalog.Elements["/OCProperties"] = ocProperties;

        var resources = page.Resources;
        var fonts = resources.Elements.GetDictionary("/Font") ?? new PdfDictionary(doc);
        fonts.Elements.SetReference("/PapF", Helvetica(doc));
        resources.Elements["/Font"] = fonts;
        var properties = resources.Elements.GetDictionary("/Properties") ?? new PdfDictionary(doc);
        properties.Elements.SetReference("/oc1", ocg);
        resources.Elements["/Properties"] = properties;

        // Prepend, so this raw content runs before PDFsharp's own coordinate transform.
        var hidden = page.Contents.PrependContent();
        byte[] ops = Encoding.ASCII.GetBytes("/OC /oc1 BDC BT /PapF 16 Tf 56 300 Td (THIS TEXT IS ON A HIDDEN LAYER) Tj ET EMC\n");
        if (hidden.Stream == null)
        {
            hidden.CreateStream(ops);
        }
        else
        {
            hidden.Stream.Value = ops;
        }

        // An attached file.
        var embedded = AddIndirect(doc, new PdfDictionary(doc));
        embedded.Elements.SetName("/Type", "/EmbeddedFile");
        embedded.CreateStream(Encoding.UTF8.GetBytes("Internal notes: the real figures are in the shared drive.\n"));
        var fileSpec = AddIndirect(doc, new PdfDictionary(doc));
        fileSpec.Elements.SetName("/Type", "/Filespec");
        fileSpec.Elements.SetString("/F", "internal-notes.txt");
        fileSpec.Elements.SetString("/UF", "internal-notes.txt");
        var ef = new PdfDictionary(doc);
        ef.Elements.SetReference("/F", embedded);
        fileSpec.Elements["/EF"] = ef;
        var embeddedFiles = new PdfDictionary(doc);
        embeddedFiles.Elements["/Names"] = new PdfArray(doc, new PdfString("internal-notes.txt"), fileSpec.ReferenceNotNull);
        var names = new PdfDictionary(doc);
        names.Elements["/EmbeddedFiles"] = embeddedFiles;
        doc.Internals.Catalog.Elements["/Names"] = names;

        // A harmless JavaScript action that runs when the file opens (Papery never runs it).
        var js = new PdfDictionary(doc);
        js.Elements.SetName("/S", "/JavaScript");
        js.Elements.SetString("/JS", "console.println(\"Papery test file opened\");");
        doc.Internals.Catalog.Elements["/OpenAction"] = js;

        doc.Save(path);
    }

    /// <summary>
    /// Personal data in every place redaction must reach: page text, text inside a
    /// form XObject, text baked into an image, and a comment.
    /// </summary>
    public static async Task RedactionTestAsync(string path, string tempDir)
    {
        // Render "SECRET-IN-IMAGE" to pixels first, so it exists only as an image.
        var label = new SharpDoc();
        var labelPage = AddPage(label, new XSize(300, 60));
        using (var g = XGraphics.FromPdfPage(labelPage))
        {
            g.DrawString("SECRET-IN-IMAGE", new XFont("Arial", 22, XFontStyleEx.Bold), Ink, new XRect(0, 0, 300, 60), XStringFormats.Center);
        }

        var labelPixels = (await Rasterizer.RenderAsync(label, tempDir, 200))[0];
        byte[] labelJpeg = await ImageEncoder.JpegAsync(labelPixels, 0.9f);

        var doc = new SharpDoc();
        doc.Info.Title = "Papery redaction test";
        var page = AddPage(doc, A4);
        double width = page.Width.Point - (2 * Margin);
        using (var g = XGraphics.FromPdfPage(page))
        {
            double y = DrawTitle(g, page, "Customer record", "Everything in coral below must be removable by redaction.");
            y = DrawWrapped(g, "Customer: Alice Smith. Email alice.smith@example.com, phone +44 20 7946 0958. " +
                "Second contact bob.jones@example.org on (555) 010-4477. Account number 12345678, sort code 01-02-03. " +
                "The code word is SECRET-PHRASE-ALPHA.", BodyFont, Ink, Margin, y, width) + 20;

            // Text inside a form XObject (a reusable drawing): redaction must look inside these too.
            var xobject = new XForm(doc, XUnit.FromPoint(320), XUnit.FromPoint(40));
            using (var fg = XGraphics.FromForm(xobject))
            {
                fg.DrawString("SECRET-IN-XOBJECT carol@example.net", LabelFont, Coral, 4, 12, XStringFormats.TopLeft);
            }

            g.DrawImage(xobject, Margin, y);
            y += 60;

            using var image = XImage.FromStream(new MemoryStream(labelJpeg));
            g.DrawImage(image, Margin, y, 225, 45);
            y += 70;

            AddNote(page, g, new XRect(Margin + width - 20, y, 20, 20), "Reviewer", "Call Alice back on alice.smith@example.com");
        }

        var page2 = AddPage(doc, A4);
        using (var g = XGraphics.FromPdfPage(page2))
        {
            double y = DrawTitle(g, page2, "Contact list");
            string[][] rows =
            [
                ["Name", "Email", "Phone"],
                ["Alice Smith", "alice.smith@example.com", "+44 20 7946 0958"],
                ["Bob Jones", "bob.jones@example.org", "(555) 010-4477"],
                ["Carol White", "carol@example.net", "+1 202 555 0143"],
                ["Dev Patel", "dev.patel@example.co.uk", "07700 900123"],
            ];
            foreach (string[] row in rows)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    g.DrawString(row[c], LabelFont, Ink, Margin + (c * width / 3), y, XStringFormats.TopLeft);
                }

                y += 22;
            }
        }

        doc.Save(path);
    }

    private static void AddNote(PdfPage page, XGraphics g, XRect rect, string author, string text)
    {
        var note = new PdfTextAnnotation
        {
            Title = author,
            Subject = "Comment",
            Contents = text,
            Icon = PdfTextAnnotationIcon.Comment,
            Rectangle = new PdfRectangle(g.Transformer.WorldToDefaultPage(rect)),
        };
        page.Annotations.Add(note);
    }

    private static PdfDictionary Helvetica(SharpDoc doc)
    {
        var font = AddIndirect(doc, new PdfDictionary(doc));
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        return font;
    }

    private static PdfDictionary AddIndirect(SharpDoc doc, PdfDictionary dict)
    {
        doc.Internals.AddObject(dict);
        return dict;
    }
}
