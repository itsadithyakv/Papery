using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// Builds a stamp annotation's look from page objects (lines, boxes and text). PDFium embeds the fonts
/// and writes the appearance itself. Everything is laid out as the user sees the page, then mapped
/// through the page's display axes, so text reads upright on rotated pages too. PDFium thread only.
/// </summary>
internal sealed unsafe class AnnotationObjects(nint document, nint page, nint annotation, PdfColor color)
{
    private const float PointSizeFloor = 4;

    // Across and down the page as displayed, in user space.
    private readonly UserPoint _across = Direction(page, 1, 0);
    private readonly UserPoint _down = Direction(page, 0, 1);

    public void Line(UserPoint from, UserPoint to, float width)
    {
        nint path = PdfiumNative.FPDFPageObj_CreateNewPath((float)from.X, (float)from.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)to.X, (float)to.Y);
        Finish(path, width);
    }

    public void Box(UserPoint bottomLeft, UserPoint topRight, float width)
    {
        nint path = PdfiumNative.FPDFPageObj_CreateNewPath((float)bottomLeft.X, (float)bottomLeft.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)topRight.X, (float)bottomLeft.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)topRight.X, (float)topRight.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)bottomLeft.X, (float)topRight.Y);
        _ = PdfiumNative.FPDFPath_Close(path);
        Finish(path, width);
    }

    /// <summary>One bold line, sized to fill the box (stamps).</summary>
    public void CentredText(string text, NormalizedRect area)
    {
        var (width, height) = AreaSize(area);
        float size = (float)Math.Max(PointSizeFloor, height * 0.55);
        double measured = Measure(text, size, bold: true);
        if (measured > width * 0.9)
        {
            size = (float)Math.Max(PointSizeFloor, size * width * 0.9 / measured);
            measured = Measure(text, size, bold: true);
        }

        var origin = At(area, (width - measured) / 2, (height + (size * 0.7)) / 2);
        Place(text, size, origin, bold: true);
    }

    /// <summary>Text wrapped to the box, top-left aligned (text boxes and callouts).</summary>
    public void WrappedText(string text, NormalizedRect area, float size)
    {
        var (width, height) = AreaSize(area);
        const double Inset = 4;
        double y = Inset + size;
        foreach (string line in Wrap(text, width - (2 * Inset), size))
        {
            if (y > height + size)
            {
                break; // ran off the box: the full words stay in the annotation's text
            }

            if (line.Length > 0)
            {
                Place(line, size, At(area, Inset, y), bold: false);
            }

            y += size * 1.25;
        }
    }

    /// <summary>A label centred on a point (measurements).</summary>
    public void Label(string text, UserPoint centre, float size)
    {
        double w = Measure(text, size, bold: false);
        var origin = new UserPoint(
            centre.X - (_across.X * w / 2) - (_down.X * size * 0.4),
            centre.Y - (_across.Y * w / 2) - (_down.Y * size * 0.4));
        Place(text, size, origin, bold: false);
    }

    public (UserPoint, UserPoint) LabelExtent(string text, UserPoint centre, float size)
    {
        double w = Measure(text, size, bold: false) / 2;
        double h = size;
        return (
            new UserPoint(centre.X - (_across.X * w) - (_down.X * h), centre.Y - (_across.Y * w) - (_down.Y * h)),
            new UserPoint(centre.X + (_across.X * w) + (_down.X * h), centre.Y + (_across.Y * w) + (_down.Y * h)));
    }

    private void Finish(nint path, float width)
    {
        _ = PdfiumNative.FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, color.A);
        _ = PdfiumNative.FPDFPageObj_SetStrokeWidth(path, width);
        _ = PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_NONE, 1);
        Append(path);
    }

    private void Append(nint pageObject)
    {
        if (PdfiumNative.FPDFAnnot_AppendObject(annotation, pageObject) == 0)
        {
            PdfiumNative.FPDFPageObj_Destroy(pageObject);
        }
    }

    private void Place(string text, float size, UserPoint origin, bool bold)
    {
        nint obj = CreateText(text, size, bold);
        if (obj == 0)
        {
            return;
        }

        // Upright as displayed: x runs across the page, y runs up the page.
        PdfiumNative.FPDFPageObj_Transform(obj, _across.X, _across.Y, -_down.X, -_down.Y, origin.X, origin.Y);
        _ = PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
        Append(obj);
    }

    private double Measure(string text, float size, bool bold)
    {
        nint obj = CreateText(text, size, bold);
        if (obj == 0)
        {
            return text.Length * size * 0.5;
        }

        float l, b, r, t;
        double width = PdfiumNative.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? r - l : text.Length * size * 0.5;
        PdfiumNative.FPDFPageObj_Destroy(obj);
        return width;
    }

    private IEnumerable<string> Wrap(string text, double width, float size)
    {
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && Measure(candidate, size, bold: false) > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            yield return line.ToString();
        }
    }

    private nint CreateText(string text, float size, bool bold)
    {
        nint font = LoadFont(text, bold);
        if (font == 0)
        {
            return 0;
        }

        nint obj = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, size);
        PdfiumNative.FPDFFont_Close(font);
        if (obj != 0)
        {
            fixed (char* t = text + '\0')
            {
                _ = PdfiumNative.FPDFText_SetText(obj, t);
            }
        }

        return obj;
    }

    /// <summary>Helvetica needs no embedding, but only covers Western letters; anything else embeds Arial.</summary>
    private nint LoadFont(string text, bool bold)
    {
        if (text.All(c => c < 256))
        {
            byte[] name = Encoding.ASCII.GetBytes((bold ? "Helvetica-Bold" : "Helvetica") + '\0');
            fixed (byte* n = name)
            {
                return PdfiumNative.FPDFText_LoadStandardFont(document, n);
            }
        }

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), bold ? "arialbd.ttf" : "arial.ttf");
        if (!File.Exists(path))
        {
            return 0;
        }

        byte[] data = File.ReadAllBytes(path);
        fixed (byte* d = data)
        {
            return PdfiumNative.FPDFText_LoadFont(document, d, (uint)data.Length, PdfiumNative.FPDF_FONT_TRUETYPE, 1);
        }
    }

    private (double Width, double Height) AreaSize(NormalizedRect area)
    {
        var a = PdfDocument.ToUser(page, new PagePoint(area.X, area.Y));
        var across = PdfDocument.ToUser(page, new PagePoint(area.Right, area.Y));
        var down = PdfDocument.ToUser(page, new PagePoint(area.X, area.Bottom));
        return (Distance(a, across), Distance(a, down));
    }

    /// <summary>A point inside an area, given in points across and down from its top-left corner.</summary>
    private UserPoint At(NormalizedRect area, double across, double down)
    {
        var a = PdfDocument.ToUser(page, new PagePoint(area.X, area.Y));
        return new UserPoint(a.X + (_across.X * across) + (_down.X * down), a.Y + (_across.Y * across) + (_down.Y * down));
    }

    private static UserPoint Direction(nint page, double dx, double dy)
    {
        var a = PdfDocument.ToUser(page, new PagePoint(0, 0));
        var b = PdfDocument.ToUser(page, new PagePoint(dx, dy));
        double length = Math.Max(0.001, Distance(a, b));
        return new UserPoint((b.X - a.X) / length, (b.Y - a.Y) / length);
    }

    private static double Distance(UserPoint a, UserPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
