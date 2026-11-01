using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Papery.Controls;
using Windows.Foundation;

namespace Papery.Services;

/// <summary>
/// A short, skippable tour: one card at a time with a sketched arrow to a real control, with Next and End tour.
/// Only ever started by the user (never pops up on its own).
/// </summary>
internal sealed class GuidedTour
{
    // Long enough for scrolling to settle, so the ring lands on the control where it ended up.
    private const int ScrollPause = 60;

    private readonly FrameworkElement _host;
    private readonly List<Step> _steps;
    private Popup? _popup;
    private TourOverlay? _overlay;
    private int _index = -1;

    public GuidedTour(FrameworkElement host, IEnumerable<Step> steps)
    {
        _host = host;
        _steps = steps.ToList();
    }

    public event EventHandler? Finished;

    public void Start()
    {
        if (_host.XamlRoot == null)
        {
            return;
        }

        // Popups don't take the theme from the page they came from, so it's passed along for the dark or light choice.
        _overlay = new TourOverlay { RequestedTheme = _host.ActualTheme };
        _overlay.NextRequested += (_, _) => Guard.Run(() => ShowStepAsync(_index + 1), "Showing the tour");
        _overlay.EndRequested += (_, _) => End();
        _popup = new Popup { XamlRoot = _host.XamlRoot, Child = _overlay, IsLightDismissEnabled = false };
        _popup.IsOpen = true;
        _host.XamlRoot.Changed += OnWindowChanged;
        Guard.Run(() => ShowStepAsync(0), "Showing the tour");
    }

    private void OnWindowChanged(XamlRoot sender, XamlRootChangedEventArgs args) => _overlay?.Arrange();

    private static bool OnScreen(Step step) => step.Target() is { Visibility: Visibility.Visible, ActualWidth: > 0 };

    private async Task ShowStepAsync(int index)
    {
        if (_overlay == null)
        {
            return;
        }

        // Skip steps whose control isn't on screen right now (e.g. sidebar hidden).
        while (index < _steps.Count && !OnScreen(_steps[index]))
        {
            index++;
        }

        if (index >= _steps.Count)
        {
            End();
            return;
        }

        _index = index;
        var step = _steps[index];
        var showing = _steps.Where(OnScreen).ToList();
        var target = step.Target()!;
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.2 });
        await Task.Delay(ScrollPause);
        if (_overlay == null)
        {
            return;
        }

        int place = showing.IndexOf(step) + 1;
        _overlay.Show(target, step.Title, step.Text, $"{place} of {showing.Count}", place == showing.Count, index + 3);
    }

    private void End()
    {
        if (_popup == null)
        {
            return;
        }

        if (_host.XamlRoot != null)
        {
            _host.XamlRoot.Changed -= OnWindowChanged;
        }

        _popup.IsOpen = false;
        _popup.Child = null;
        _popup = null;
        _overlay = null;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One stop on the tour. The target is looked up when shown, so it is always current.</summary>
    public sealed record Step(Func<FrameworkElement?> Target, string Title, string Text);
}
