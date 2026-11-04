using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Papery.Services;

/// <summary>Keeps track of open Papery windows and which one was used last.</summary>
internal static class WindowManager
{
    private static readonly List<MainWindow> Windows = [];

    public static MainWindow? Active { get; private set; }

    public static int Count => Windows.Count;

    public static IReadOnlyList<MainWindow> All => Windows;

    public static MainWindow Create(bool withHomeTab = true)
    {
        var window = new MainWindow(withHomeTab);
        Windows.Add(window);
        Active ??= window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                Active = window;
            }
        };
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            if (Active == window)
            {
                Active = Windows.LastOrDefault();
            }

            if (Windows.Count > 0)
            {
                SessionService.Remember(); // its tabs went with it; the last window's are kept as it closes
            }
        };
        return window;
    }

    /// <summary>
    /// Where files opened from Explorer go: the window used last, if it's on screen; otherwise any window on screen.
    /// A window that was never shown (or is closing) is passed over, so files never land somewhere out of sight.
    /// </summary>
    public static MainWindow? ForNewFiles() =>
        new[] { Active }.Concat(Windows.AsEnumerable().Reverse()).FirstOrDefault(w => w != null && w.AppWindow.IsVisible);

    public static MainWindow? Find(WindowId id) => Windows.FirstOrDefault(w => w.AppWindow.Id == id);

    /// <summary>The window an element is currently shown in (elements can move between windows with their tab).</summary>
    public static MainWindow? WindowFor(UIElement element) =>
        Windows.FirstOrDefault(w => w.Hosts(element))
        ?? Windows.FirstOrDefault(w => w.Content?.XamlRoot != null && ReferenceEquals(w.Content.XamlRoot, element.XamlRoot));
}
