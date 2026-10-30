using System.Globalization;
using System.Text;

namespace Papery.Pdf.Editing;

/// <summary>A point in PDF user space (points, y up).</summary>
internal readonly record struct UserPoint(double X, double Y, float Pressure = 1);

/// <summary>
/// Writes the drawing instructions (a PDF "appearance stream") for each kind of annotation.
/// </summary>
/// <remarks>
/// A PDF annotation without an appearance leaves each viewer to invent one, and they don't agree.
/// With one, Edge, Adobe and Papery all draw the same thing. Everything here is in page
/// coordinates and pure text, so it is unit-tested without PDFium.
/// </remarks>
internal static class AnnotationAppearance
{
    public static string TextMark(PdfAnnotationKind kind, PdfColor color, IReadOnlyList<(UserPoint BottomLeft, UserPoint TopRight)> lines)
    {
        var b = Start(color);
        if (kind == PdfAnnotationKind.Highlight)
        {
            b.Append(Fill(color));
            foreach (var (bl, tr) in lines)
            {
                b.Append(N(bl.X)).Append(N(bl.Y)).Append(N(tr.X - bl.X)).Append(N(tr.Y - bl.Y)).Append("re f\n");
            }

            return b.ToString();
        }

        b.Append(Stroke(color));
        foreach (var (bl, tr) in lines)
        {
            double height = tr.Y - bl.Y, thick = Math.Max(0.6, height * 0.07);
            b.Append(N(thick)).Append("w\n");
            if (kind == PdfAnnotationKind.StrikeOut)
            {
                double y = bl.Y + (height * 0.45);
                b.Append(N(bl.X)).Append(N(y)).Append("m ").Append(N(tr.X)).Append(N(y)).Append("l S\n");
            }
            else if (kind == PdfAnnotationKind.Underline)
            {
                double y = bl.Y + thick;
                b.Append(N(bl.X)).Append(N(y)).Append("m ").Append(N(tr.X)).Append(N(y)).Append("l S\n");
            }
            else
            {
                // Squiggly: a small zigzag along the bottom of the line.
                double step = Math.Max(2, height * 0.16), low = bl.Y + thick, high = low + step;
                b.Append(N(bl.X)).Append(N(low)).Append("m ");
                bool up = true;
                for (double x = bl.X + step; x < tr.X; x += step, up = !up)
                {
                    b.Append(N(x)).Append(N(up ? high : low)).Append("l ");
                }

                b.Append("S\n");
            }
        }

        return b.ToString();
    }

    public static string Strokes(PdfColor color, IReadOnlyList<IReadOnlyList<UserPoint>> strokes, float width)
    {
        var b = Start(color).Append(Stroke(color)).Append("1 J 1 j\n");
        foreach (var stroke in strokes)
        {
            if (stroke.Count == 1)
            {
                // A single tap is a dot.
                b.Append(N(width)).Append("w ").Append(N(stroke[0].X)).Append(N(stroke[0].Y)).Append("m ").Append(N(stroke[0].X)).Append(N(stroke[0].Y)).Append("l S\n");
                continue;
            }

            if (stroke.All(p => Math.Abs(p.Pressure - 1) < 0.001f))
            {
                b.Append(N(width)).Append("w ").Append(N(stroke[0].X)).Append(N(stroke[0].Y)).Append("m ");
                for (int i = 1; i < stroke.Count; i++)
                {
                    b.Append(N(stroke[i].X)).Append(N(stroke[i].Y)).Append("l ");
                }

                b.Append("S\n");
                continue;
            }

            // Pressure: each short segment gets its own thickness.
            for (int i = 1; i < stroke.Count; i++)
            {
                float pressure = (stroke[i - 1].Pressure + stroke[i].Pressure) / 2;
                b.Append(N(width * Math.Clamp(pressure * 1.5, 0.25, 1.5))).Append("w ")
                    .Append(N(stroke[i - 1].X)).Append(N(stroke[i - 1].Y)).Append("m ")
                    .Append(N(stroke[i].X)).Append(N(stroke[i].Y)).Append("l S\n");
            }
        }

        return b.ToString();
    }

    public static string Rectangle(PdfColor color, UserPoint bottomLeft, UserPoint topRight, float width) =>
        Start(color).Append(Stroke(color)).Append(N(width)).Append("w ")
            .Append(N(bottomLeft.X)).Append(N(bottomLeft.Y)).Append(N(topRight.X - bottomLeft.X)).Append(N(topRight.Y - bottomLeft.Y)).Append("re S\n").ToString();

    public static string Ellipse(PdfColor color, UserPoint bottomLeft, UserPoint topRight, float width)
    {
        // Four Bézier curves, the standard way to draw an ellipse in PDF.
        const double K = 0.5522847498;
        double cx = (bottomLeft.X + topRight.X) / 2, cy = (bottomLeft.Y + topRight.Y) / 2;
        double rx = (topRight.X - bottomLeft.X) / 2, ry = (topRight.Y - bottomLeft.Y) / 2;
        var b = Start(color).Append(Stroke(color)).Append(N(width)).Append("w ");
        b.Append(N(cx + rx)).Append(N(cy)).Append("m ");
        Curve(b, cx + rx, cy + (K * ry), cx + (K * rx), cy + ry, cx, cy + ry);
        Curve(b, cx - (K * rx), cy + ry, cx - rx, cy + (K * ry), cx - rx, cy);
        Curve(b, cx - rx, cy - (K * ry), cx - (K * rx), cy - ry, cx, cy - ry);
        Curve(b, cx + (K * rx), cy - ry, cx + rx, cy - (K * ry), cx + rx, cy);
        return b.Append("h S\n").ToString();
    }

    /// <summary>A line with a filled arrowhead at its end.</summary>
    public static string Arrow(PdfColor color, UserPoint from, UserPoint to, float width)
    {
        var (left, right) = ArrowHead(from, to, width);
        return Start(color).Append(Stroke(color)).Append(Fill(color)).Append("1 J ").Append(N(width)).Append("w ")
            .Append(N(from.X)).Append(N(from.Y)).Append("m ").Append(N(to.X)).Append(N(to.Y)).Append("l S\n")
            .Append(N(to.X)).Append(N(to.Y)).Append("m ").Append(N(left.X)).Append(N(left.Y)).Append("l ")
            .Append(N(right.X)).Append(N(right.Y)).Append("l h f\n").ToString();
    }

    /// <summary>The two back corners of an arrowhead (its size grows with the line width).</summary>
    public static (UserPoint Left, UserPoint Right) ArrowHead(UserPoint from, UserPoint to, float width)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, length = Math.Max(0.01, Math.Sqrt((dx * dx) + (dy * dy)));
        double ux = dx / length, uy = dy / length, size = Math.Max(8, width * 5), half = size * 0.45;
        double baseX = to.X - (ux * size), baseY = to.Y - (uy * size);
        return (new UserPoint(baseX - (uy * half), baseY + (ux * half)), new UserPoint(baseX + (uy * half), baseY - (ux * half)));
    }

    public static string Polygon(PdfColor color, IReadOnlyList<UserPoint> points, float width)
    {
        var b = Start(color).Append(Stroke(color)).Append("1 j ").Append(N(width)).Append("w ");
        b.Append(N(points[0].X)).Append(N(points[0].Y)).Append("m ");
        foreach (var p in points.Skip(1))
        {
            b.Append(N(p.X)).Append(N(p.Y)).Append("l ");
        }

        return b.Append("h S\n").ToString();
    }

    /// <summary>The bumpy outline used to call attention to something on plans and drawings.</summary>
    public static string Cloud(PdfColor color, UserPoint bottomLeft, UserPoint topRight, float width)
    {
        double w = topRight.X - bottomLeft.X, h = topRight.Y - bottomLeft.Y;
        double bump = Math.Clamp(Math.Min(w, h) / 6, 6, 18);
        int across = Math.Max(2, (int)(w / bump)), down = Math.Max(2, (int)(h / bump));
        var corners = new List<UserPoint>();
        for (int i = 0; i < across; i++)
        {
            corners.Add(new UserPoint(bottomLeft.X + (w * i / across), bottomLeft.Y));
        }

        for (int i = 0; i < down; i++)
        {
            corners.Add(new UserPoint(topRight.X, bottomLeft.Y + (h * i / down)));
        }

        for (int i = 0; i < across; i++)
        {
            corners.Add(new UserPoint(topRight.X - (w * i / across), topRight.Y));
        }

        for (int i = 0; i < down; i++)
        {
            corners.Add(new UserPoint(bottomLeft.X, topRight.Y - (h * i / down)));
        }

        double cx = (bottomLeft.X + topRight.X) / 2, cy = (bottomLeft.Y + topRight.Y) / 2;
        var b = Start(color).Append(Stroke(color)).Append("1 j ").Append(N(width)).Append("w ");
        b.Append(N(corners[0].X)).Append(N(corners[0].Y)).Append("m ");
        for (int i = 0; i < corners.Count; i++)
        {
            var from = corners[i];
            var to = corners[(i + 1) % corners.Count];
            double mx = (from.X + to.X) / 2, my = (from.Y + to.Y) / 2;
            double ox = mx - cx, oy = my - cy, length = Math.Max(0.01, Math.Sqrt((ox * ox) + (oy * oy)));
            double bx = mx + (ox / length * bump * 0.55), by = my + (oy / length * bump * 0.55); // bulge outwards
            Curve(b, bx, by, bx, by, to.X, to.Y);
        }

        return b.Append("S\n").ToString();
    }

    /// <summary>A small speech-bubble note icon, filled with the note's colour.</summary>
    public static string NoteIcon(PdfColor color, UserPoint topLeft, double size)
    {
        double l = topLeft.X, t = topLeft.Y, r = l + size, bottom = t - (size * 0.78), tail = t - size;
        var b = Start(color).Append(Fill(color)).Append("0.2 0.2 0.2 RG 0.6 w ");
        b.Append(N(l)).Append(N(t)).Append("m ").Append(N(r)).Append(N(t)).Append("l ")
            .Append(N(r)).Append(N(bottom)).Append("l ").Append(N(l + (size * 0.5))).Append(N(bottom)).Append("l ")
            .Append(N(l + (size * 0.25))).Append(N(tail)).Append("l ").Append(N(l + (size * 0.25))).Append(N(bottom)).Append("l ")
            .Append(N(l)).Append(N(bottom)).Append("l h B\n");
        return b.ToString();
    }

    private static void Curve(StringBuilder b, double x1, double y1, double x2, double y2, double x3, double y3) =>
        b.Append(N(x1)).Append(N(y1)).Append(N(x2)).Append(N(y2)).Append(N(x3)).Append(N(y3)).Append("c ");

    /// <summary>"/GS gs" picks up the transparency PDFium attaches from the annotation's colour.</summary>
    private static StringBuilder Start(PdfColor color) => new(color.A < 255 ? "/GS gs\n" : string.Empty);

    private static string Fill(PdfColor c) => $"{N(c.R / 255.0)}{N(c.G / 255.0)}{N(c.B / 255.0)}rg\n";

    private static string Stroke(PdfColor c) => $"{N(c.R / 255.0)}{N(c.G / 255.0)}{N(c.B / 255.0)}RG\n";

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + " ";
}
