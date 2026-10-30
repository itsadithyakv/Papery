using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// Builds a stamp annotation's look from page objects (lines, boxes and text). PDFium embeds the fonts
/// and writes the appearance itself. Everything is laid out as the user sees the page, then mapped
/// through the page's display axes, so text reads upright on rotated pages too. With no annotation
/// (0) the objects go straight onto the page instead. PDFium thread only.
/// </summary>
internal sealed unsafe class AnnotationObjects(nint document, nint page, nint annotation, PdfColor color, FontBook? fonts = null)
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

    /// <summary>A word nobody sees but search and selection can find, stretched to cover the word in the picture.</summary>
    public void InvisibleWord(string text, NormalizedRect area)
    {
        var (width, height) = AreaSize(area);
        float size = (float)Math.Max(1, height);
        double measured = Measure(text, size, default);

        // The space runs into the gap after the word. Without it, search and copy read "twowords".
        nint obj = CreateText(text + " ", size, default);
        if (obj == 0 || measured <= 0)
        {
            if (obj != 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(obj);
            }

            return;
        }

        double stretch = width / measured;
        var origin = At(area, 0, height * 0.8); // the baseline, roughly: letters sit above it and descenders below
        PdfiumNative.FPDFPageObj_Transform(obj, _across.X * stretch, _across.Y * stretch, -_down.X, -_down.Y, origin.X, origin.Y);
        _ = PdfiumNative.FPDFTextObj_SetTextRenderMode(obj, PdfiumNative.FPDF_TEXTRENDERMODE_INVISIBLE);
        Append(obj);
    }

    /// <summary>A solid box with no outline (whiteout).</summary>
    public void FilledBox(UserPoint bottomLeft, UserPoint topRight)
    {
        nint path = PdfiumNative.FPDFPageObj_CreateNewPath((float)bottomLeft.X, (float)bottomLeft.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)topRight.X, (float)bottomLeft.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)topRight.X, (float)topRight.Y);
        _ = PdfiumNative.FPDFPath_LineTo(path, (float)bottomLeft.X, (float)topRight.Y);
        _ = PdfiumNative.FPDFPath_Close(path);
        _ = PdfiumNative.FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, color.A);
        _ = PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_WINDING, 0);
        Append(path);
    }

    /// <summary>One line of text centred on a point given in points from the page's top-left, turned anticlockwise.</summary>
    public void StampText(string text, double centreAcross, double centreDown, float size, double angleDegrees, TextLook look)
    {
        double width = Measure(text, size, look);
        double angle = angleDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);

        // Along the text and up from it, in user space.
        double ux = (_across.X * cos) - (_down.X * sin), uy = (_across.Y * cos) - (_down.Y * sin);
        double vx = (-_down.X * cos) - (_across.X * sin), vy = (-_down.Y * cos) - (_across.Y * sin);
        var origin = PdfDocument.ToUser(page, new PagePoint(0, 0));
        double cx = origin.X + (_across.X * centreAcross) + (_down.X * centreDown);
        double cy = origin.Y + (_across.Y * centreAcross) + (_down.Y * centreDown);
        double midHeight = size * 0.35;
        nint obj = CreateText(text, size, look);
        if (obj == 0)
        {
            return;
        }

        PdfiumNative.FPDFPageObj_Transform(obj, ux, uy, vx, vy, cx - (ux * width / 2) - (vx * midHeight), cy - (uy * width / 2) - (vy * midHeight));
        _ = PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
        Append(obj);
    }

    /// <summary>The page's size in points, as displayed.</summary>
    public (double Width, double Height) PageSize() => AreaSize(new NormalizedRect(0, 0, 1, 1));

    /// <summary>How wide a line of text will be, in points.</summary>
    public double TextWidth(string text, float size, TextLook look) => Measure(text, size, look);

    /// <summary>One bold line, sized to fill the box (stamps).</summary>
    public void CentredText(string text, NormalizedRect area)
    {
        var (width, height) = AreaSize(area);
        float size = (float)Math.Max(PointSizeFloor, height * 0.55);
        var look = new TextLook(Bold: true);
        double measured = Measure(text, size, look);
        if (measured > width * 0.9)
        {
            size = (float)Math.Max(PointSizeFloor, size * width * 0.9 / measured);
            measured = Measure(text, size, look);
        }

        var origin = At(area, (width - measured) / 2, (height + (size * 0.7)) / 2);
        Place(text, size, origin, look);
    }

    /// <summary>Text wrapped to the box (text boxes and callouts).</summary>
    public void WrappedText(string text, NormalizedRect area, float size, TextLook look = default, PdfTextAlign align = PdfTextAlign.Left)
    {
        var (width, height) = AreaSize(area);
        const double Inset = 4;
        double y = Inset + size;
        foreach (string line in Wrap(text, width - (2 * Inset), size, look))
        {
            if (y > height + size)
            {
                break; // ran off the box: the full words stay in the annotation's text
            }

            if (line.Length > 0)
            {
                double x = align switch
                {
                    PdfTextAlign.Centre => (width - Measure(line, size, look)) / 2,
                    PdfTextAlign.Right => width - Inset - Measure(line, size, look),
                    _ => Inset,
                };
                Place(line, size, At(area, x, y), look);
            }

            y += size * 1.25;
        }
    }

    /// <summary>A label centred on a point (measurements).</summary>
    public void Label(string text, UserPoint centre, float size)
    {
        double w = Measure(text, size, default);
        var origin = new UserPoint(
            centre.X - (_across.X * w / 2) - (_down.X * size * 0.4),
            centre.Y - (_across.Y * w / 2) - (_down.Y * size * 0.4));
        Place(text, size, origin, default);
    }

    public (UserPoint, UserPoint) LabelExtent(string text, UserPoint centre, float size)
    {
        double w = Measure(text, size, default) / 2;
        double h = size;
        return (
            new UserPoint(centre.X - (_across.X * w) - (_down.X * h), centre.Y - (_across.Y * w) - (_down.Y * h)),
            new UserPoint(centre.X + (_across.X * w) + (_down.X * h), centre.Y + (_across.Y * w) + (_down.Y * h)));
    }

    /// <summary>The four corners of a picture placed in an area and turned about its middle.</summary>
    public List<UserPoint> PictureCorners(NormalizedRect area, float degrees)
    {
        var (origin, ax, ay, dx, dy) = PictureFrame(area, degrees);
        return
        [
            origin,
            new UserPoint(origin.X + ax, origin.Y + ay),
            new UserPoint(origin.X + dx, origin.Y + dy),
            new UserPoint(origin.X + ax + dx, origin.Y + ay + dy),
        ];
    }

    /// <summary>Places a picture (transparent pixels stay see-through, so the page shows behind a signature).</summary>
    public void Picture(PdfBitmap picture, NormalizedRect area, float degrees)
    {
        nint image = PdfiumNative.FPDFPageObj_NewImageObj(document);
        fixed (byte* pixels = picture.Pixels)
        {
            nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(picture.Width, picture.Height, PdfiumNative.FPDFBitmap_BGRA, (nint)pixels, picture.Stride);
            _ = PdfiumNative.FPDFImageObj_SetBitmap(null, 0, image, bitmap);
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
        }

        // An image fills the unit square: x along its bottom edge, y up its side.
        var (origin, ax, ay, dx, dy) = PictureFrame(area, degrees);
        _ = PdfiumNative.FPDFImageObj_SetMatrix(image, ax, ay, -dx, -dy, origin.X + dx, origin.Y + dy);
        Append(image);
    }

    /// <summary>Places a picture that's already a JPEG, without decoding it. Returns false if PDFium won't take it.</summary>
    public bool PictureFromJpeg(byte[] jpeg, NormalizedRect area)
    {
        nint image = PdfiumNative.FPDFPageObj_NewImageObj(document);
        if (image == 0 || !ImageToPdfWriter.SetPixels(image, new PdfImageData(jpeg, null)))
        {
            return false;
        }

        var (origin, ax, ay, dx, dy) = PictureFrame(area, 0);
        _ = PdfiumNative.FPDFImageObj_SetMatrix(image, ax, ay, -dx, -dy, origin.X + dx, origin.Y + dy);
        Append(image);
        return true;
    }

    /// <summary>Top-left corner of a turned area, and its across and down edges, in user space.</summary>
    private (UserPoint Origin, double Ax, double Ay, double Dx, double Dy) PictureFrame(NormalizedRect area, float degrees)
    {
        var (width, height) = AreaSize(area);
        double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);

        // Turned clockwise on screen: "across" tips towards "down".
        double acrossX = (_across.X * cos) + (_down.X * sin), acrossY = (_across.Y * cos) + (_down.Y * sin);
        double downX = (_down.X * cos) - (_across.X * sin), downY = (_down.Y * cos) - (_across.Y * sin);
        var centre = At(area, width / 2, height / 2);
        var origin = new UserPoint(
            centre.X - (acrossX * width / 2) - (downX * height / 2),
            centre.Y - (acrossY * width / 2) - (downY * height / 2));
        return (origin, acrossX * width, acrossY * width, downX * height, downY * height);
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
        if (annotation == 0)
        {
            PdfiumNative.FPDFPage_InsertObject(page, pageObject);
        }
        else if (PdfiumNative.FPDFAnnot_AppendObject(annotation, pageObject) == 0)
        {
            PdfiumNative.FPDFPageObj_Destroy(pageObject);
        }
    }

    private void Place(string text, float size, UserPoint origin, TextLook look)
    {
        nint obj = CreateText(text, size, look);
        if (obj == 0)
        {
            return;
        }

        // Upright as displayed: x runs across the page, y runs up the page.
        PdfiumNative.FPDFPageObj_Transform(obj, _across.X, _across.Y, -_down.X, -_down.Y, origin.X, origin.Y);
        _ = PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
        Append(obj);
    }

    private double Measure(string text, float size, TextLook look)
    {
        nint obj = CreateText(text, size, look);
        if (obj == 0)
        {
            return text.Length * size * 0.5;
        }

        float l, b, r, t;
        double width = PdfiumNative.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? r - l : text.Length * size * 0.5;
        PdfiumNative.FPDFPageObj_Destroy(obj);
        return width;
    }

    private IEnumerable<string> Wrap(string text, double width, float size, TextLook look)
    {
        // Text boxes on Windows end lines with a lone \r, which a PDF font would show as a stray character.
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && Measure(candidate, size, look) > width)
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

    private nint CreateText(string text, float size, TextLook look)
    {
        // A book keeps fonts open and shared; otherwise each piece of text has its own.
        nint font = fonts != null ? fonts.Get(look, text.All(c => c < 256), () => LoadFont(text, look)) : LoadFont(text, look);
        if (font == 0)
        {
            return 0;
        }

        nint obj = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, size);
        if (fonts == null)
        {
            PdfiumNative.FPDFFont_Close(font);
        }

        if (obj != 0)
        {
            fixed (char* t = text + '\0')
            {
                _ = PdfiumNative.FPDFText_SetText(obj, t);
            }
        }

        return obj;
    }

    /// <summary>The standard fonts need no embedding, but only cover Western letters; anything else embeds Windows' matching font.</summary>
    private nint LoadFont(string text, TextLook look)
    {
        if (text.All(c => c < 256))
        {
            byte[] name = Encoding.ASCII.GetBytes(StandardFontName(look) + '\0');
            fixed (byte* n = name)
            {
                return PdfiumNative.FPDFText_LoadStandardFont(document, n);
            }
        }

        string stem = look.Font switch
        {
            PdfTextFont.Serif => "times",
            PdfTextFont.Mono => "cour",
            _ => "arial",
        };
        string style = (look.Bold, look.Italic) switch
        {
            (true, true) => "bi",
            (true, false) => "bd",
            (false, true) => "i",
            _ => string.Empty,
        };
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), $"{stem}{style}.ttf");
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

    private static string StandardFontName(TextLook look) => look.Font switch
    {
        PdfTextFont.Serif => (look.Bold, look.Italic) switch
        {
            (true, true) => "Times-BoldItalic",
            (true, false) => "Times-Bold",
            (false, true) => "Times-Italic",
            _ => "Times-Roman",
        },
        PdfTextFont.Mono => "Courier" + Suffix(look),
        _ => "Helvetica" + Suffix(look),
    };

    private static string Suffix(TextLook look) => (look.Bold, look.Italic) switch
    {
        (true, true) => "-BoldOblique",
        (true, false) => "-Bold",
        (false, true) => "-Oblique",
        _ => string.Empty,
    };

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

/// <summary>Which font a piece of text uses.</summary>
internal readonly record struct TextLook(PdfTextFont Font = PdfTextFont.Sans, bool Bold = false, bool Italic = false);
