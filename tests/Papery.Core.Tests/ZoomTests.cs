using Papery.Core.Viewing;

namespace Papery.Core.Tests;

public sealed class ZoomTests
{
    [Theory]
    [InlineData(1.0, 1, 1.25)]
    [InlineData(1.0, -1, 0.75)]
    [InlineData(1.1, 1, 1.25)]
    [InlineData(1.1, -1, 1.0)]
    [InlineData(16.0, 1, 16.0)]
    [InlineData(0.1, -1, 0.1)]
    public void Step_moves_to_the_next_stop(double current, int direction, double expected) =>
        Assert.Equal(expected, Zoom.Step(current, direction), 3);

    [Fact]
    public void Fit_width_and_fit_page()
    {
        Assert.Equal(2.0, Zoom.FitWidth(1600, 800), 3);
        // Height is the limit: 1000 / (800 + 2*10)
        Assert.Equal(1000.0 / 820, Zoom.FitPage(2000, 1000, 600, 800, 10), 3);
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.0001, Zoom.Min)]
    [InlineData(1000.0, Zoom.Max)]
    public void Clamp_keeps_zoom_sane(double input, double expected) => Assert.Equal(expected, Zoom.Clamp(input));

    [Fact]
    public void Label_is_a_whole_percentage() => Assert.Equal("133%", Zoom.Label(4.0 / 3));
}
