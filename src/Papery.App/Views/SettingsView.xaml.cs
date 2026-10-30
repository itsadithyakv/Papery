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
        AlwaysCopySwitch.IsOn = s.AlwaysSaveCopy;
        VersionText.Text = $"Version {AppVersion()}";
#if DEBUG
        // The Free/Pro switch exists only in developer builds; Release (Store) builds don't compile this.
        DeveloperSection.Visibility = Visibility.Visible;
        DebugLicenseBox.SelectedIndex = (int)AppServices.Pro.DebugMode;
#endif
        UpdateProCard();
        Loaded += (_, _) => AppServices.Pro.Changed += OnProChanged;
        Unloaded += (_, _) => AppServices.Pro.Changed -= OnProChanged;
        _loading = false;
    }

    private void OnProChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateProCard);

    private void UpdateProCard()
    {
        bool pro = AppServices.Pro.IsPro;
        ProMascot.Mood = pro ? Controls.MascotMood.Excited : Controls.MascotMood.Happy;
        ProTitle.Text = pro ? "Papery Pro is unlocked" : "Free version";
        ProSubtitle.Text = pro
            ? "Thank you! Every tool is yours, on every PC signed in with your Microsoft account."
            : "Unlock every tool once. No subscription, ever.";
        BuyButton.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnBuyClick(object sender, RoutedEventArgs e) => await ProDialogs.RequireProAsync(XamlRoot, null);

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        RestoreButton.Content = "Checking…";
        var result = await AppServices.Store.CheckAsync();
        AppServices.Pro.Apply(result);
        RestoreButton.Content = "Restore purchase";
        RestoreButton.IsEnabled = true;
        UpdateProCard();
        if (AppServices.Pro.IsPro)
        {
            ProSubtitle.Text = "Restored. Welcome back!";
            ProMascot.Bounce();
        }
        else
        {
            ProSubtitle.Text = !Core.Licensing.StoreIds.IsConfigured
                ? "This build isn't connected to the Microsoft Store yet (Store ID placeholder)."
                : result == Core.Licensing.StoreCheckResult.Unknown
                    ? "Couldn't reach the Microsoft Store. Check your connection and try again."
                    : "No Papery Pro purchase found for the Microsoft account signed in to the Store.";
        }
    }

    private void OnDebugLicenseChanged(object sender, SelectionChangedEventArgs e)
    {
#if DEBUG
        if (_loading)
        {
            return;
        }

        var mode = (Core.Licensing.DebugLicenseMode)DebugLicenseBox.SelectedIndex;
        AppServices.Settings.Update(s => s.DebugLicenseMode = mode);
        AppServices.Pro.SetDebugMode(mode);
#endif
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.Theme = (AppTheme)ThemeBox.SelectedIndex);

    private void OnReadingChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.ReadingMode = (ReadingMode)ReadingBox.SelectedIndex);

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e) =>
        Save(s => s.DefaultViewMode = (ViewMode)LayoutBox.SelectedIndex);

    private void OnRememberToggled(object sender, RoutedEventArgs e) =>
        Save(s => s.RememberPosition = RememberSwitch.IsOn);

    private void OnAlwaysCopyToggled(object sender, RoutedEventArgs e) =>
        Save(s => s.AlwaysSaveCopy = AlwaysCopySwitch.IsOn);

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
