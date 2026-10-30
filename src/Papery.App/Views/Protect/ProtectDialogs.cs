using System.Security.Cryptography.X509Certificates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Protect;

internal static class ProtectDialogs
{
    private static TextBlock ErrorLine() => new()
    {
        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>A password to add, and what people may do once they've opened it.</summary>
    public static async Task<PdfProtection?> AskPasswordAsync(XamlRoot root)
    {
        var first = new PasswordBox { Header = "Password" };
        var second = new PasswordBox { Header = "Type it again" };
        var print = new CheckBox { Content = "Allow printing", IsChecked = true };
        var copy = new CheckBox { Content = "Allow copying text", IsChecked = true };
        var edit = new CheckBox { Content = "Allow changes", IsChecked = true };
        var error = ErrorLine();
        var note = new TextBlock
        {
            Text = "Keep it safe: nobody, including Papery, can open the file without it.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        };
        var panel = new StackPanel { Spacing = 10, Width = 340 };
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(print);
        panel.Children.Add(copy);
        panel.Children.Add(edit);
        panel.Children.Add(note);
        panel.Children.Add(error);
        var dialog = new ContentDialog { Title = "Add a password", Content = panel, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            string? problem = first.Password.Length == 0 ? "Type a password." : first.Password != second.Password ? "The two passwords don't match." : null;
            if (problem != null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };

        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? new PdfProtection(first.Password, print.IsChecked == true, copy.IsChecked == true, edit.IsChecked == true)
            : null;
    }

    public static async Task<PdfDescription?> AskMetadataAsync(XamlRoot root, PdfMetadata current)
    {
        var title = new TextBox { Header = "Title", Text = current.Title ?? string.Empty };
        var author = new TextBox { Header = "Author", Text = current.Author ?? string.Empty };
        var subject = new TextBox { Header = "Subject", Text = current.Subject ?? string.Empty };
        var keywords = new TextBox { Header = "Keywords", Text = current.Keywords ?? string.Empty };
        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(title);
        panel.Children.Add(author);
        panel.Children.Add(subject);
        panel.Children.Add(keywords);
        var dialog = new ContentDialog { Title = "Document details", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? new PdfDescription(title.Text, author.Text, subject.Text, keywords.Text)
            : null;
    }

    public static async Task<PdfHiddenDataChoice?> AskHiddenDataAsync(XamlRoot root, PdfHiddenData found)
    {
        CheckBox Row(string text, int count, bool always = false) => new()
        {
            Content = always || count > 0 ? (count > 0 ? $"{text} ({count:N0})" : text) : $"{text} (none found)",
            IsEnabled = always || count > 0,
            IsChecked = always || count > 0,
        };

        var metadata = Row("Details like author and title", 0, found.HasMetadata);
        var comments = Row("Comments and markup", found.Comments);
        var attachments = Row("Attached files", found.Attachments);
        var layers = Row("Hidden layers", found.HiddenLayers);
        var scripts = Row("Scripts", found.Scripts);
        if (!found.HasMetadata)
        {
            metadata.Content = "Details like author and title (none found)";
            metadata.IsEnabled = false;
            metadata.IsChecked = false;
        }

        var panel = new StackPanel { Spacing = 6, Width = 360 };
        panel.Children.Add(metadata);
        panel.Children.Add(comments);
        panel.Children.Add(attachments);
        panel.Children.Add(layers);
        panel.Children.Add(scripts);
        if (found.HiddenLayers > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Removing hidden layers turns every page into a picture, with its words kept searchable.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
            });
        }

        var dialog = new ContentDialog { Title = "Remove hidden data", Content = panel, PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? new PdfHiddenDataChoice(metadata.IsChecked == true, comments.IsChecked == true, attachments.IsChecked == true, layers.IsChecked == true, scripts.IsChecked == true)
            : null;
    }

    /// <summary>What to look for across the document when marking redactions.</summary>
    public static async Task<(bool Emails, bool Phones, string Words)?> AskRedactSearchAsync(XamlRoot root)
    {
        var emails = new CheckBox { Content = "Email addresses" };
        var phones = new CheckBox { Content = "Phone numbers" };
        var words = new TextBox { Header = "Words or names", PlaceholderText = "Exact words to find" };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(emails);
        panel.Children.Add(phones);
        panel.Children.Add(words);
        var dialog = new ContentDialog { Title = "Find what to remove", Content = panel, PrimaryButtonText = "Mark them", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? (emails.IsChecked == true, phones.IsChecked == true, words.Text.Trim())
            : null;
    }

    /// <summary>Choosing a certificate. The name says plainly what this is, and how it differs from a picture of a signature.</summary>
    public static async Task<PdfSignatureRequest?> AskCertifiedSignatureAsync(XamlRoot root, IReadOnlyList<X509Certificate2> certificates)
    {
        if (certificates.Count == 0)
        {
            await Dialogs.ShowMessageAsync(root, "No signing certificate found", "A certified digital signature needs a certificate in Windows, usually from your employer or a certificate authority. None that can sign was found on this PC. You can still add a picture of your signature in Fill & Sign.");
            return null;
        }

        var picker = new ComboBox { Header = "Certificate", MinWidth = 320 };
        foreach (var certificate in certificates)
        {
            picker.Items.Add($"{certificate.GetNameInfo(X509NameType.SimpleName, false)}, until {certificate.NotAfter:d}");
        }

        picker.SelectedIndex = 0;
        var reason = new TextBox { Header = "Reason (optional)" };
        var place = new TextBox { Header = "Place (optional)" };
        var panel = new StackPanel { Spacing = 10, Width = 400 };
        panel.Children.Add(picker);
        panel.Children.Add(reason);
        panel.Children.Add(place);
        panel.Children.Add(new TextBlock
        {
            Text = "This proves who signed and shows if the file changes afterwards. Whether another person's PDF app trusts it depends on who issued your certificate. Any later change to the PDF breaks the signature, so sign last.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        });
        var dialog = new ContentDialog { Title = "Certified digital signature", Content = panel, PrimaryButtonText = "Sign", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary
            ? new PdfSignatureRequest(certificates[Math.Max(0, picker.SelectedIndex)], string.IsNullOrWhiteSpace(reason.Text) ? null : reason.Text, string.IsNullOrWhiteSpace(place.Text) ? null : place.Text)
            : null;
    }
}
