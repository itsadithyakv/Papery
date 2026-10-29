namespace Papery.Core.Viewing;

/// <summary>Zoom levels and the "fit" calculations. 1.0 = 100% = real paper size on a 96 DPI screen.</summary>
public static class Zoom
{
    public const double Min = 0.1;
    public const double Max = 16.0;

    /// <summary>The stops used by Ctrl + plus/minus and the zoom menu.</summary>
    public static readonly IReadOnlyList<double> Steps =
        [0.1, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0];

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, Min, Max) : 1.0;

    /// <summary>Zoom so the widest row fills the viewport width.</summary>
    public static double FitWidth(double viewportWidth, double contentWidth) =>
        contentWidth <= 0 ? 1.0 : Clamp(viewportWidth / contentWidth);

    /// <summary>Zoom so a whole row (page or spread, plus gaps) fits in the viewport.</summary>
    public static double FitPage(double viewportWidth, double viewportHeight, double rowWidth, double rowHeight, double gap) =>
        rowWidth <= 0 || rowHeight <= 0
            ? 1.0
            : Clamp(Math.Min(viewportWidth / (rowWidth + (2 * gap)), viewportHeight / (rowHeight + (2 * gap))));

    /// <summary>The next stop above (or below, for <paramref name="direction"/> &lt; 0) the current zoom.</summary>
    public static double Step(double current, int direction)
    {
        const double epsilon = 0.001;
        if (direction > 0)
        {
            return Steps.FirstOrDefault(s => s > current + epsilon, Max);
        }

        return Steps.LastOrDefault(s => s < current - epsilon, Min);
    }

    /// <summary>"125%"-style label.</summary>
    public static string Label(double zoom) => $"{Math.Round(zoom * 100):0}%";
}
