using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Controls;
using Papery.Pdf;
using Papery.Services;

namespace Papery.Views.Edit;

/// <summary>What the link dialog decided.</summary>
internal sealed record LinkChoice(string? Uri, int? TargetPage, bool Remove);

/// <summary>Where a link goes: a web address or a page of this PDF.</summary>
internal static class LinkDialog
{
    public static async Task<LinkChoice?> ShowAsync(XamlRoot root, PdfLink? existing, int pageCount)
    {
        var kinds = new PillBar();
        kinds.AddItem("Web address", "", "web");
        kinds.AddItem("Page", "", "page");

        var address = new TextBox { Text = existing?.Uri ?? string.Empty, PlaceholderText = "https://" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(address, "Web address");
        var page = EditPieces.Number((existing?.TargetPage ?? 0) + 1, 1, pageCount, $"Page (1 to {pageCount:N0})", 150);
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(kinds);
        panel.Children.Add(address);
        panel.Children.Add(page);
        panel.Children.Add(error);

        void ShowKind()
        {
            bool web = kinds.SelectedIndex != 1;
            address.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
            page.Visibility = web ? Visibility.Collapsed : Visibility.Visible;
        }

        kinds.SelectionChanged += (_, _) => ShowKind();
        kinds.SelectedIndex = existing?.TargetPage != null ? 1 : 0;
        ShowKind();

        var dialog = new ContentDialog
        {
            Title = existing == null ? "New link" : "Link",
            Content = panel,
            PrimaryButtonText = "Done",
            SecondaryButtonText = existing == null ? string.Empty : "Remove link",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        string? uri = null;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (kinds.SelectedIndex == 1)
            {
                return;
            }

            uri = Normalise(address.Text);
            if (uri == null)
            {
                error.Text = "That doesn't look like a web address.";
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };

        return await Dialogs.ShowAsync(dialog, root) switch
        {
            ContentDialogResult.Primary => kinds.SelectedIndex == 1
                ? new LinkChoice(null, (int)(double.IsNaN(page.Value) ? 0 : page.Value - 1), false)
                : new LinkChoice(uri, null, false),
            ContentDialogResult.Secondary => new LinkChoice(null, null, true),
            _ => null,
        };
    }

    /// <summary>Accepts "example.com" as well as a full address, but only ones that are safe to open.</summary>
    internal static string? Normalise(string typed)
    {
        string text = typed.Trim();
        if (text.Length == 0 || text.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal) && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Contains('@', StringComparison.Ordinal) ? $"mailto:{text}" : $"https://{text}";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto" ? parsed.AbsoluteUri : null;
    }
}
