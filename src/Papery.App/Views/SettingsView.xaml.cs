using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Core.Storage;
using Papery.Core.Viewing;
using Papery.Services;
using Windows.ApplicationModel;
using Windows.System;

namespace Papery.Views;

public sealed partial class SettingsView : UserControl
{
    private bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        var s = AppServices.Settings.Current;
        ThemeBox.SelectedIndex = (int)s.Theme;
        ReadingBox.SelectedIndex = (int)s.ReadingMode;
        LayoutBox.SelectedIndex = (int)s.DefaultViewMode;
        RememberSwitch.IsOn = s.RememberPosition;
        VersionText.Text = $"Version {AppVersion()}";
        _loading = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.Theme = (AppTheme)ThemeBox.SelectedIndex);

    private void OnReadingChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.ReadingMode = (ReadingMode)ReadingBox.SelectedIndex);

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.DefaultViewMode = (ViewMode)LayoutBox.SelectedIndex);

    private void OnRememberToggled(object sender, RoutedEventArgs e) =>
        Save(s => s.RememberPosition = RememberSwitch.IsOn);

    private void Save(Action<AppSettings> change)
    {
        if (!_loading)
        {
            AppServices.Settings.Update(change);
        }
    }

    private async void OnDefaultAppsClick(object sender, RoutedEventArgs e)
    {
        // Opens Papery's own page in Windows' Default apps settings where supported, otherwise the main page.
        try
        {
            string aumid = $"{Package.Current.Id.FamilyName}!App";
            if (await Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(aumid)}")))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // No package identity.
        }

        await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    private void OnClearRecentClick(object sender, RoutedEventArgs e)
    {
        AppServices.ViewState.ClearRecent();
        _ = AppServices.ViewState.SaveAsync();
    }

    private async void OnNoticesClick(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        string text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "Third-party notices are missing from this build.";
        var content = new ScrollViewer
        {
            MaxHeight = 480,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
        };
        await Dialogs.ShowAsync(new ContentDialog { Title = "Third-party notices", Content = content, CloseButtonText = "Close" }, XamlRoot);
    }

    private static string AppVersion()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            return "development build";
        }
    }
}
