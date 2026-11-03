using System.Globalization;
using Papery.Core.Measuring;

namespace Papery.Core.Tests;

public sealed class MeasurementTests
{
    private const double Cm = Measurement.PointsPerCentimetre;

    // A 3 × 4 cm rectangle, in points.
    private static readonly (double X, double Y)[] Rectangle = [(0, 0), (3 * Cm, 0), (3 * Cm, 4 * Cm), (0, 4 * Cm)];

    [Fact]
    public void A_rectangle_has_width_times_height() =>
        Assert.Equal(12, Measurement.SquareCentimetres(Measurement.Area(Rectangle)), 6);

    [Fact]
    public void The_way_round_does_not_matter() =>
        Assert.Equal(Measurement.Area(Rectangle), Measurement.Area(Rectangle.Reverse().ToArray()), 6);

    [Fact]
    public void A_triangle_is_half_its_box() =>
        Assert.Equal(6, Measurement.SquareCentimetres(Measurement.Area([(0, 0), (4 * Cm, 0), (0, 3 * Cm)])), 6);

    [Fact]
    public void An_l_shape_leaves_out_its_corner()
    {
        // A 4 × 4 square with a 2 × 2 bite out of one corner.
        (double X, double Y)[] shape = [(0, 0), (4 * Cm, 0), (4 * Cm, 2 * Cm), (2 * Cm, 2 * Cm), (2 * Cm, 4 * Cm), (0, 4 * Cm)];
        Assert.Equal(12, Measurement.SquareCentimetres(Measurement.Area(shape)), 6);
    }

    [Fact]
    public void Fewer_than_three_corners_have_no_area()
    {
        Assert.Equal(0, Measurement.Area([]));
        Assert.Equal(0, Measurement.Area([(0, 0), (10, 10)]));
    }

    [Fact]
    public void Perimeter_goes_all_the_way_round_only_when_closed()
    {
        Assert.Equal(14, Measurement.Centimetres(Measurement.Length(Rectangle, closed: true)), 6);
        Assert.Equal(10, Measurement.Centimetres(Measurement.Length(Rectangle)), 6);
    }

    [Fact]
    public void A_single_line_is_its_distance() =>
        Assert.Equal(5, Measurement.Centimetres(Measurement.Length([(0, 0), (3 * Cm, 4 * Cm)])), 6);

    [Fact]
    public void An_inch_is_two_and_a_half_centimetres() => Assert.Equal(2.54, Measurement.Centimetres(72), 6);

    [Fact]
    public void Labels_are_in_centimetres_to_one_place()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("12.3 cm", Measurement.LengthLabel(12.34 * Cm));
            Assert.Equal("12.0 cm²", Measurement.AreaLabel(Measurement.Area(Rectangle)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
