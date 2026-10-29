using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Commands;
using Papery.Controls;
using Papery.Core.Licensing;
using Papery.Services;
using Papery.Services.Licensing;

namespace Papery.Views;

/// <summary>
/// The Papery Pro screens: one calm "unlock" screen (never a nag; only shown when the user picks a
/// Pro tool or presses Buy), and the "Welcome to Papery Pro" moment after buying.
/// </summary>
internal static class ProDialogs
{
    /// <summary>Returns true if Pro is (now) unlocked.</summary>
    public static async Task<bool> RequireProAsync(XamlRoot root, ToolInfo? tool)
    {
        if (AppServices.Pro.IsPro)
        {
            return true;
        }

        bool bought = await ShowUnlockAsync(root, tool);
        if (bought)
        {
            await ShowWelcomeAsync(root);
        }

        return AppServices.Pro.IsPro;
    }

    /// <summary>The unlock screen. Returns true if the purchase completed.</summary>
    public static async Task<bool> ShowUnlockAsync(XamlRoot root, ToolInfo? tool)
    {
        var panel = new StackPanel { Spacing = 16, Width = 480 };

        // Character + title
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Mascot { Width = 56, Height = 66 });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Papery Pro", FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = tool != null ? $"{tool.Name} is part of Pro. Every tool, yours to keep." : "Every tool, yours to keep.",
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        panel.Children.Add(header);

        // What's included: the tools themselves, not a paragraph.
        panel.Children.Add(ToolTiles(ToolCatalog.All.Where(t => t.IsPro)));

        // Price and the promise
        var priceText = new TextBlock { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Text = " " };
        var loading = new ProgressRing { IsActive = true, Width = 18, Height = 18 };
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        priceRow.Children.Add(loading);
        priceRow.Children.Add(priceText);
        var promise = new StackPanel { Spacing = 2 };
        promise.Children.Add(priceRow);
        promise.Children.Add(new TextBlock { Text = "One payment. No subscription. Ever.", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        promise.Children.Add(new TextBlock
        {
            Text = "Works on all your PCs signed in with the same Microsoft account.",
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
            FontSize = 12,
        });
        panel.Children.Add(promise);

        var status = new InfoBar { IsOpen = false, IsClosable = false };
        panel.Children.Add(status);

        var restore = new HyperlinkButton { Content = "Already bought it? Restore purchase", Padding = new Thickness(0) };
        panel.Children.Add(restore);

        var dialog = new ContentDialog
        {
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "Buy",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        bool purchased = false;
        dialog.Opened += async (_, _) =>
        {
            string? price = await AppServices.Store.GetPriceAsync();
            loading.IsActive = false;
            loading.Visibility = Visibility.Collapsed;
            priceText.Text = price ?? "Price shown by the Microsoft Store";
            priceText.FontSize = price != null ? 22 : 15;
            dialog.PrimaryButtonText = price != null ? $"Buy for {price}" : "Buy";
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // Keep the dialog up while the Store window is open; close only on success.
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            Show(status, "Waiting for the Microsoft Store…", InfoBarSeverity.Informational);
            var outcome = await AppServices.Store.PurchaseAsync();
            dialog.IsPrimaryButtonEnabled = true;
            switch (outcome)
            {
                case PurchaseOutcome.Purchased or PurchaseOutcome.AlreadyOwned:
                    AppServices.Pro.Apply(StoreCheckResult.Owned);
                    purchased = true;
                    dialog.Hide();
                    break;
                case PurchaseOutcome.Cancelled:
                    Show(status, "No problem. Nothing was charged, and your work is just as you left it.", InfoBarSeverity.Informational);
                    break;
                case PurchaseOutcome.NetworkProblem:
                    Show(status, "Papery couldn't reach the Microsoft Store. Check your internet connection and try again.", InfoBarSeverity.Warning);
                    break;
                case PurchaseOutcome.NotConfigured:
#if DEBUG
                    // Developer builds with no Store ID yet: simulate a purchase so the whole flow can be tried.
                    AppServices.Pro.SetDebugMode(DebugLicenseMode.ForcePro);
                    AppServices.Settings.Update(s => s.DebugLicenseMode = DebugLicenseMode.ForcePro);
                    purchased = true;
                    dialog.Hide();
#else
                    Show(status, "This build isn't connected to the Microsoft Store yet (the add-on Store ID is still a placeholder).", InfoBarSeverity.Warning);
#endif
                    break;
                default:
                    Show(status, "The Microsoft Store couldn't finish the purchase. You haven't been charged. Please try again in a moment.", InfoBarSeverity.Warning);
                    break;
            }

            deferral.Complete();
        };

        restore.Click += async (_, _) =>
        {
            Show(status, "Checking your purchases…", InfoBarSeverity.Informational);
            AppServices.Pro.Apply(await AppServices.Store.CheckAsync());
            if (AppServices.Pro.IsPro)
            {
                purchased = true;
                dialog.Hide();
            }
            else
            {
                Show(status, "No Papery Pro purchase was found for this Microsoft account. Make sure you're signed in to the Store with the account you used.", InfoBarSeverity.Informational);
            }
        };

        await Dialogs.ShowAsync(dialog, root);
        return purchased;
    }

    /// <summary>A warm thank-you that shows what's now unlocked.</summary>
    public static async Task ShowWelcomeAsync(XamlRoot root)
    {
        var mascot = new Mascot { Mood = MascotMood.Excited, Width = 72, Height = 86, HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { Spacing = 14, Width = 480 };
        panel.Children.Add(mascot);
        panel.Children.Add(new TextBlock
        {
            Text = "Welcome to Papery Pro",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Everything is unlocked, for good. Thank you for supporting Papery!",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        });
        var tiles = ToolTiles(ToolCatalog.All.Where(t => t.IsPro), animate: true);
        panel.Children.Add(tiles);

        var dialog = new ContentDialog { Content = panel, PrimaryButtonText = "Let's go", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => mascot.Bounce();
        await Dialogs.ShowAsync(dialog, root);
    }

    /// <summary>A compact grid of tool icons in their family colours, with short names.</summary>
    private static VariableSizedWrapGrid ToolTiles(IEnumerable<ToolInfo> tools, bool animate = false)
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 158, ItemHeight = 44 };
        int index = 0;
        foreach (var tool in tools)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = tool.TintSubtleBrush };
            tile.Child = new FontIcon
            {
                Glyph = tool.Glyph,
                FontSize = 15,
                Foreground = tool.TintBrush,
                FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"],
            };
            row.Children.Add(tile);
            row.Children.Add(new TextBlock { Text = tool.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            grid.Children.Add(row);
            if (animate)
            {
                Motion.Entrance(row, 120 + (index++ * 35));
            }
        }

        return grid;
    }

    private static void Show(InfoBar bar, string message, InfoBarSeverity severity)
    {
        bar.Message = message;
        bar.Severity = severity;
        bar.IsOpen = true;
    }
}
