using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Papery.Services;

/// <summary>
/// Looks up a theme colour for the theme an element is actually showing.
/// </summary>
/// <remarks>
/// Papery's own Light/Dark setting can differ from Windows', and a plain
/// Application.Current.Resources[key] lookup in code always answers for the Windows theme.
/// XAML "{ThemeResource ...}" handles this by itself; code that builds UI must use this helper
/// (and call it again when the element's ActualThemeChanged fires).
/// </remarks>
internal static class ThemeResources
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static Brush Brush(FrameworkElement element, string key) => (Brush)Lookup(element, key);

    public static object Lookup(FrameworkElement element, string key)
    {
        string theme = Accessibility.HighContrast ? "HighContrast" : element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed)
                && themed is ResourceDictionary d
                && d.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return Application.Current.Resources[key];
    }
}
