using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>
/// PDFium can't make polygon or polyline annotations, so Papery draws areas and perimeters as ink and this
/// turns them into the real thing in the saved copy. Other apps then know them as measurements, with their
/// corners and scale, and still draw Papery's own appearance. Nothing else in the file is changed.
/// </summary>
internal static class MeasuredShapes
{
    // How far a point is in centimetres, for the scale other apps read.
    private const double CentimetresPerPoint = 2.54 / 72;

    /// <summary>Writes <paramref name="from"/> to <paramref name="to"/> with the named ink changed. False if PDFsharp couldn't.</summary>
    public static bool TryWrite(string from, string to, IReadOnlySet<string> names)
    {
        try
        {
            using var doc = PdfReader.Open(from, PdfDocumentOpenMode.Modify);
            foreach (var page in doc.Pages)
            {
                // Read the list rather than page.Annotations, which would add an empty one to every page.
                var annotations = page.Elements.GetArray("/Annots");
                for (int i = 0; i < (annotations?.Elements.Count ?? 0); i++)
                {
                    if (annotations!.Elements.GetDictionary(i) is { } annotation
                        && annotation.Elements.GetName("/Subtype") == "/Ink"
                        && names.Contains(annotation.Elements.GetString("/NM")))
                    {
                        Reshape(doc, annotation);
                    }
                }
            }

            // Open (not Create): the temp file is hidden, and Create refuses to overwrite hidden files.
            using var stream = new FileStream(to, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
            stream.SetLength(0);
            doc.Save(stream, closeStream: false);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception)
        {
            // Whatever PDFsharp trips over in a file, saving mustn't fail for it: the ink copy is kept, and looks the same.
            return false;
        }
    }

    private static void Reshape(PdfSharp.Pdf.PdfDocument doc, PdfDictionary annotation)
    {
        bool area = annotation.Elements.GetString("/Subj") == nameof(PdfAnnotationKind.Area);
        if (annotation.Elements.GetArray("/InkList")?.Elements.GetArray(0) is not { } ink || ink.Elements.Count < 4)
        {
            return;
        }

        var numbers = Enumerable.Range(0, ink.Elements.Count).Select(ink.Elements.GetReal).ToList();

        // The ink went back to the first corner to look closed; a polygon closes itself.
        if (area && numbers.Count >= 8 && numbers[^2] == numbers[0] && numbers[^1] == numbers[1])
        {
            numbers.RemoveRange(numbers.Count - 2, 2);
        }

        annotation.Elements.Remove("/InkList");
        annotation.Elements.SetName("/Subtype", area ? "/Polygon" : "/PolyLine");
        annotation.Elements["/Vertices"] = new PdfArray(doc, numbers.Select(n => (PdfItem)new PdfReal(n)).ToArray());
        annotation.Elements.SetName("/IT", area ? "/PolygonDimension" : "/PolyLineDimension");
        annotation.Elements["/Measure"] = Scale(doc);
    }

    /// <summary>One point on the page is a point on paper, given in centimetres to one place, as the label is.</summary>
    private static PdfDictionary Scale(PdfSharp.Pdf.PdfDocument doc)
    {
        var measure = new PdfDictionary(doc);
        measure.Elements.SetName("/Type", "/Measure");
        measure.Elements.SetName("/Subtype", "/RL");
        measure.Elements.SetString("/R", "1 cm = 1 cm");
        measure.Elements["/X"] = new PdfArray(doc, Format(doc, "cm", CentimetresPerPoint));
        measure.Elements["/D"] = new PdfArray(doc, Format(doc, "cm", 1));
        measure.Elements["/A"] = new PdfArray(doc, Format(doc, "sq cm", 1));
        return measure;
    }

    private static PdfDictionary Format(PdfSharp.Pdf.PdfDocument doc, string unit, double factor)
    {
        var format = new PdfDictionary(doc);
        format.Elements.SetName("/Type", "/NumberFormat");
        format.Elements.SetString("/U", unit);
        format.Elements.SetReal("/C", factor);
        format.Elements.SetInteger("/D", 10);
        return format;
    }
}
