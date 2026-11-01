using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Papery.Services;

/// <summary>
/// Shapes that look drawn by hand: a little wobble, and ends that overshoot as a pen does. The wobble comes from
/// a seed, so a sketch stays the same on every redraw instead of jittering.
/// </summary>
internal static class Doodle
{
    private const double Wobble = 2.2;
    private const double PointSpacing = 7;

    /// <summary>The whole window dark, with a rounded hole that leaves one area lit.</summary>
    public static Geometry ScrimWithHole(Size window, Rect hole)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, window.Width, window.Height) });
        group.Children.Add(new PathGeometry
        {
            Figures = { Figure(RoundedOutline(hole, Corner(hole), 0, 1), 0, 0, closed: true) },
        });
        return group;
    }

    /// <summary>A loop around an area that overshoots where it started, like a circle drawn on paper.</summary>
    public static Geometry Ring(Rect area, int seed)
    {
        var points = RoundedOutline(area, Corner(area), -0.02, 1.05);
        return new PathGeometry { Figures = { Figure(points, seed, Wobble, closed: false) } };
    }

    /// <summary>A curved arrow from the edge of a card to the edge of the ring, with a two-stroke head.</summary>
    public static Geometry Arrow(Rect from, Rect to, int seed)
    {
        var start = NearestEdge(from, Middle(to));
        var end = NearestEdge(to, start);
        double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 12)
        {
            return new PathGeometry();
        }

        // Start and stop a little short, so it floats between the card and the ring.
        const double Gap = 7;
        start = new Point(start.X + (dx / length * Gap), start.Y + (dy / length * Gap));
        end = new Point(end.X - (dx / length * Gap), end.Y - (dy / length * Gap));

        double bend = length * 0.22 * (seed % 2 == 0 ? 1 : -1);
        var control = new Point(((start.X + end.X) / 2) - (dy / length * bend), ((start.Y + end.Y) / 2) + (dx / length * bend));
        int steps = Math.Max(8, (int)(length / PointSpacing));
        var line = new List<Point>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            line.Add(new Point((u * u * start.X) + (2 * u * t * control.X) + (t * t * end.X), (u * u * start.Y) + (2 * u * t * control.Y) + (t * t * end.Y)));
        }

        // The head follows the direction the line is travelling as it arrives.
        double angle = Math.Atan2(end.Y - control.Y, end.X - control.X);
        const double HeadLength = 17;
        Point Barb(double turn) => new(end.X - (HeadLength * Math.Cos(angle + turn)), end.Y - (HeadLength * Math.Sin(angle + turn)));

        var geometry = new PathGeometry();
        geometry.Figures.Add(Figure(line, seed + 1, Wobble * 0.7, closed: false, keepEnds: true));
        geometry.Figures.Add(Figure([Barb(0.55), end, Barb(-0.62)], seed + 2, 0, closed: false));
        return geometry;
    }

    /// <summary>A short wavy underline.</summary>
    public static Geometry Squiggle(double width, int seed)
    {
        var points = new List<Point>();
        for (double x = 0; x <= width; x += 3)
        {
            points.Add(new Point(x, 4 + (2.6 * Math.Sin((x / 7.5) + seed))));
        }

        return new PathGeometry { Figures = { Figure(points, seed, 0, closed: false) } };
    }

    private static double Corner(Rect area) => Math.Min(16, Math.Min(area.Width, area.Height) / 2);

    private static Point Middle(Rect area) => new(area.X + (area.Width / 2), area.Y + (area.Height / 2));

    /// <summary>Where the line from a rectangle's middle towards a point crosses its edge.</summary>
    private static Point NearestEdge(Rect area, Point towards)
    {
        var middle = Middle(area);
        double dx = towards.X - middle.X, dy = towards.Y - middle.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return middle;
        }

        double scale = Math.Min(Math.Abs(dx) < 0.001 ? double.MaxValue : area.Width / 2 / Math.Abs(dx), Math.Abs(dy) < 0.001 ? double.MaxValue : area.Height / 2 / Math.Abs(dy));
        return new Point(middle.X + (dx * scale), middle.Y + (dy * scale));
    }

    /// <summary>Points round a rounded rectangle, clockwise from the top left. <paramref name="from"/> and <paramref name="to"/> are shares of the way round, and past 1 goes round again.</summary>
    private static List<Point> RoundedOutline(Rect area, double corner, double from, double to)
    {
        double straightX = Math.Max(0, area.Width - (2 * corner)), straightY = Math.Max(0, area.Height - (2 * corner));
        double arc = Math.PI * corner / 2;
        double perimeter = (2 * straightX) + (2 * straightY) + (4 * arc);
        int count = Math.Max(24, (int)(perimeter * (to - from) / PointSpacing));
        var points = new List<Point>(count + 1);
        for (int i = 0; i <= count; i++)
        {
            double share = from + ((to - from) * i / count);
            share -= Math.Floor(share);
            points.Add(OnOutline(area, corner, share * perimeter, straightX, straightY, arc));
        }

        return points;
    }

    private static Point OnOutline(Rect a, double r, double along, double straightX, double straightY, double arc)
    {
        Point Curve(double cx, double cy, double startAngle, double travelled)
        {
            double angle = startAngle + (travelled / Math.Max(arc, 0.001) * Math.PI / 2);
            return new Point(cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle)));
        }

        if (along < straightX)
        {
            return new Point(a.Left + r + along, a.Top);
        }

        along -= straightX;
        if (along < arc)
        {
            return Curve(a.Right - r, a.Top + r, -Math.PI / 2, along);
        }

        along -= arc;
        if (along < straightY)
        {
            return new Point(a.Right, a.Top + r + along);
        }

        along -= straightY;
        if (along < arc)
        {
            return Curve(a.Right - r, a.Bottom - r, 0, along);
        }

        along -= arc;
        if (along < straightX)
        {
            return new Point(a.Right - r - along, a.Bottom);
        }

        along -= straightX;
        if (along < arc)
        {
            return Curve(a.Left + r, a.Bottom - r, Math.PI / 2, along);
        }

        along -= arc;
        if (along < straightY)
        {
            return new Point(a.Left, a.Bottom - r - along);
        }

        along -= straightY;
        return Curve(a.Left + r, a.Top + r, Math.PI, along);
    }

    /// <summary>Joins points into a line, pushing each a little off true with slow waves, which reads as a steady hand rather than a shaky one.</summary>
    private static PathFigure Figure(IReadOnlyList<Point> points, int seed, double wobble, bool closed, bool keepEnds = false)
    {
        Point Off(int index)
        {
            double fade = keepEnds ? Math.Sin(Math.PI * index / Math.Max(1, points.Count - 1)) : 1;
            double phase = index * 0.17;
            double x = (Math.Sin(phase + seed) + (0.5 * Math.Sin((phase * 2.3) + (seed * 3)))) * wobble * fade;
            double y = (Math.Cos((phase * 0.9) + (seed * 2)) + (0.5 * Math.Sin((phase * 1.9) + seed))) * wobble * fade;
            return new Point(points[index].X + x, points[index].Y + y);
        }

        var line = new PolyLineSegment();
        for (int i = 1; i < points.Count; i++)
        {
            line.Points.Add(Off(i));
        }

        var figure = new PathFigure { StartPoint = Off(0), IsClosed = closed, IsFilled = closed };
        figure.Segments.Add(line);
        return figure;
    }
}
