using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Papery.Services;

namespace Papery.Controls;

/// <summary>Good-news toast. Call <see cref="Show"/>; it hides itself after a few seconds.</summary>
public sealed partial class Toast : UserControl
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private Action? _action;
    private int _shownCount;

    public Toast()
    {
        InitializeComponent();
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Hide();

        // Keep it up while the pointer is over it, so the action button can be reached calmly.
        Card.PointerEntered += (_, _) => _hideTimer.Stop();
        Card.PointerExited += (_, _) => _hideTimer.Start();
    }

    /// <param name="glyph">Icon: a tick by default.</param>
    /// <param name="actionText">Optional button, e.g. "Open folder" or "Undo".</param>
    public void Show(string message, string glyph = "", string? actionText = null, Action? action = null, int seconds = 5)
    {
        _shownCount++;
        MessageText.Text = message;
        Icon.Glyph = glyph;
        _action = action;
        ActionButton.Content = actionText;
        ActionButton.Visibility = actionText != null && action != null ? Visibility.Visible : Visibility.Collapsed;

        Visibility = Visibility.Visible;
        Motion.SlideTo(this, 24, 0, 1); // start just below, invisible...
        Motion.SlideTo(this, 0, 1);     // ...then slide up and fade in
        Motion.Pop(IconCircle, 1.25f);

        // Narrator reads the message without moving focus.
        FrameworkElementAutomationPeer.FromElement(MessageText)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, message, "papery-toast");

        _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Hide()
    {
        _hideTimer.Stop();
        int shown = _shownCount;
        Motion.SlideTo(this, 24, 0, 180);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(Motion.Enabled ? 200 : 0);
            if (shown == _shownCount)
            {
                Visibility = Visibility.Collapsed; // not if a newer toast appeared meanwhile
            }
        });
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _action?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
