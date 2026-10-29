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
        };
        return window;
    }

    public static MainWindow? Find(WindowId id) => Windows.FirstOrDefault(w => w.AppWindow.Id == id);

    /// <summary>The window an element is currently shown in (elements can move between windows with their tab).</summary>
    public static MainWindow? WindowFor(UIElement element) =>
        Windows.FirstOrDefault(w => w.Content?.XamlRoot != null && w.Content.XamlRoot == element.XamlRoot);
}
