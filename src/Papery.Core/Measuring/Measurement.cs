namespace Papery.Core.Measuring;

/// <summary>
/// The sums behind the measuring tools. Points are PDF points (1/72 inch) on the page as displayed, so the
/// answers are what a ruler held to the printed page would say, in centimetres.
/// </summary>
public static class Measurement
{
    public const double PointsPerCentimetre = 72 / 2.54;

    /// <summary>Along the points in order. Closed adds the way back to the first (a perimeter all round).</summary>
    public static double Length(IReadOnlyList<(double X, double Y)> points, bool closed = false)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        return closed && points.Count > 2 ? total + Distance(points[^1], points[0]) : total;
    }

    /// <summary>
    /// The area inside the points, by the shoelace formula. Clockwise or not gives the same answer;
    /// if the outline crosses itself, the loops that go the other way round are taken off.
    /// </summary>
    public static double Area(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double twice = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            twice += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(twice) / 2;
    }

    public static double Centimetres(double points) => points / PointsPerCentimetre;

    public static double SquareCentimetres(double squarePoints) => squarePoints / (PointsPerCentimetre * PointsPerCentimetre);

    /// <summary>"12.3 cm", as the distance tool has always written it.</summary>
    public static string LengthLabel(double points) => $"{Centimetres(points):0.0} cm";

    /// <summary>"12.3 cm²". The ² is in every PDF's standard fonts, so nothing extra gets embedded.</summary>
    public static string AreaLabel(double squarePoints) => $"{SquareCentimetres(squarePoints):0.0} cm²";

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}
