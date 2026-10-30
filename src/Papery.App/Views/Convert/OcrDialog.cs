using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Pages;
using Papery.Services;
using Windows.Globalization;

namespace Papery.Views.Convert;

internal sealed record OcrChoice(Language Language, IReadOnlyList<int> Pages);

/// <summary>Which language to read and which pages, for making a scan searchable.</summary>
internal static class OcrDialog
{
    public static async Task<OcrChoice?> ShowAsync(XamlRoot root, int pageCount, Language? remembered)
    {
        var languages = OcrService.Languages;
        if (languages.Count == 0)
        {
            var none = new ContentDialog
            {
                Title = "No reading languages installed",
                Content = Dialogs.Wrap("Papery uses the text recognition built into Windows, and it needs a language with Optical Character Recognition installed. You can add one in Windows Settings."),
                PrimaryButtonText = "Open Settings",
                CloseButtonText = "Close",
            };
            if (await Dialogs.ShowAsync(none, root) == ContentDialogResult.Primary)
            {
                OcrService.OpenLanguageSettings();
            }

            return null;
        }

        var picker = new ComboBox { Header = "Language", MinWidth = 240 };
        foreach (var language in languages)
        {
            picker.Items.Add(language.DisplayName);
        }

        string? wanted = (remembered ?? OcrService.DefaultLanguage)?.LanguageTag;
        picker.SelectedIndex = Math.Max(0, languages.ToList().FindIndex(l => l.LanguageTag == wanted));

        var more = new HyperlinkButton { Content = "Add more languages", Padding = new Thickness(0) };
        more.Click += (_, _) => OcrService.OpenLanguageSettings();
        var range = new TextBox { Header = "Pages", PlaceholderText = "All, or like 1-3, 7" };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Spacing = 10, Width = 340 };
        panel.Children.Add(picker);
        panel.Children.Add(more);
        panel.Children.Add(range);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Make searchable",
            Content = panel,
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        IReadOnlyList<int> pages = Enumerable.Range(0, pageCount).ToArray();
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(range.Text) && !PageRanges.TryParse(range.Text, pageCount, out pages, out string? problem))
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };

        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? new OcrChoice(languages[Math.Max(0, picker.SelectedIndex)], pages)
            : null;
    }
}
