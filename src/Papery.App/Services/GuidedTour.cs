using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Papery.Services;

/// <summary>
/// A short, skippable tour: one tip at a time pointing at a real control, with Next and End tour.
/// Only ever started by the user (never pops up on its own).
/// </summary>
internal sealed class GuidedTour
{
    private readonly Panel _host;
    private readonly List<Step> _steps;
    private TeachingTip? _tip;
    private int _index = -1;
    private bool _moving;

    public GuidedTour(Panel host, IEnumerable<Step> steps)
    {
        _host = host;
        _steps = steps.ToList();
    }

    public event EventHandler? Finished;

    public void Start()
    {
        _tip = new TeachingTip
        {
            IsLightDismissEnabled = false,
            PreferredPlacement = TeachingTipPlacementMode.Auto,
            CloseButtonContent = "End tour",
        };
        _tip.ActionButtonClick += (_, _) => ShowStep(_index + 1);
        _tip.CloseButtonClick += (_, _) => End();
        _tip.Closed += (_, args) =>
        {
            // Esc closes the tip: treat that as ending the tour (but not our own re-targeting).
            if (!_moving && args.Reason != TeachingTipCloseReason.CloseButton)
            {
                End();
            }
        };
        _host.Children.Add(_tip);
        ShowStep(0);
    }

    private void ShowStep(int index)
    {
        if (_tip == null)
        {
            return;
        }

        // Skip steps whose control isn't on screen right now (e.g. sidebar hidden).
        while (index < _steps.Count && _steps[index].Target() is not { Visibility: Visibility.Visible, ActualWidth: > 0 })
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
        int remaining = _steps.Skip(index + 1).Count(s => s.Target() is { Visibility: Visibility.Visible, ActualWidth: > 0 });

        _moving = true;
        _tip.IsOpen = false;
        _tip.Target = step.Target();
        _tip.Title = step.Title;
        _tip.Subtitle = step.Text;
        _tip.ActionButtonContent = remaining == 0 ? "Finish" : "Next";
        _tip.Content = new TextBlock
        {
            Text = $"Step {index + 1} of {_steps.Count}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.7,
        };
        _tip.IsOpen = true;
        _moving = false;
    }

    private void End()
    {
        if (_tip == null)
        {
            return;
        }

        var tip = _tip;
        _tip = null;
        _moving = true;
        tip.IsOpen = false;
        _host.Children.Remove(tip);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One stop on the tour. The target is looked up when shown, so it is always current.</summary>
    public sealed record Step(Func<FrameworkElement?> Target, string Title, string Text);
}
