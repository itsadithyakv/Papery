using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Protect;

/// <summary>The two "what's in this file?" screens: its attached files and its digital signatures.</summary>
internal static class FileExtrasDialogs
{
    private static Style Secondary => (Style)Application.Current.Resources["PaperySecondaryTextStyle"];

    private static TextBlock Line(string text, Style? style = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style != null)
        {
            block.Style = style;
        }

        return block;
    }

    /// <summary>Lists attachments to save. Only PDFs open in Papery: anything else could be a program, so it's never run from here.</summary>
    public static async Task ShowAttachmentsAsync(XamlRoot root, PdfDocument document, nint hwnd, Action<string> openPdf, Action<string> saved)
    {
        var attachments = await document.GetAttachmentsAsync();
        var list = new StackPanel { Spacing = 10, Width = 460 };
        var dialog = new ContentDialog { Title = "Attached files", CloseButtonText = "Done", Content = new ScrollViewer { Content = list, MaxHeight = 420 } };
        foreach (var attachment in attachments)
        {
            string name = Path.GetFileName(attachment.Name.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Attachment {attachment.Index + 1}";
            }

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(Line(Tools.CompressDialog.Size(attachment.Size), Secondary));
            row.Children.Add(words);

            var save = new Button { Content = "Save" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(save, $"Save {name}");
            save.Click += (_, _) => Guard.Run(async () =>
            {
                string extension = Path.GetExtension(name);
                if (await Pickers.PickSaveAsync(hwnd, Path.GetFileNameWithoutExtension(name), "Attached file", string.IsNullOrEmpty(extension) ? ".bin" : extension) is { } output)
                {
                    await File.WriteAllBytesAsync(output, await document.ReadAttachmentAsync(attachment.Index));
                    saved(output);
                }
            }, "Saving an attached file");
            Grid.SetColumn(save, 2);
            row.Children.Add(save);

            if (string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var open = new Button { Content = "Open" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, $"Open {name}");
                open.Click += (_, _) => Guard.Run(async () =>
                {
                    string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "attachments", Guid.NewGuid().ToString("N"))).FullName;
                    string path = Path.Combine(folder, name);
                    await File.WriteAllBytesAsync(path, await document.ReadAttachmentAsync(attachment.Index));
                    dialog.Hide();
                    openPdf(path);
                }, "Opening an attached file");
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
            }

            list.Children.Add(row);
        }

        if (attachments.Count == 0)
        {
            list.Children.Add(Line("This PDF has no attached files."));
        }

        await Dialogs.ShowAsync(dialog, root);
    }

    /// <summary>One plain sentence on what each signature proves, and a clear word on what it doesn't.</summary>
    public static async Task ShowSignaturesAsync(XamlRoot root, IReadOnlyList<PdfSignatureCheck> checks)
    {
        var list = new StackPanel { Spacing = 16, Width = 460 };
        foreach (var check in checks)
        {
            var item = new StackPanel { Spacing = 3 };
            var (glyph, headline) = Headline(check);
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            top.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
            top.Children.Add(new TextBlock { Text = headline, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 });
            item.Children.Add(top);

            string who = check.Signer ?? "Unknown signer";
            string when = check.SignedAt is { } at ? $", {at.LocalDateTime:d MMMM yyyy, HH:mm}" : string.Empty;
            item.Children.Add(Line(who + when, Secondary));
            if (!string.IsNullOrWhiteSpace(check.Reason))
            {
                item.Children.Add(Line($"Reason: {check.Reason}", Secondary));
            }

            if (check.CanCheck && check.Intact)
            {
                item.Children.Add(Line(check.TrustedSigner
                    ? "Windows trusts this signer's certificate."
                    : "Windows doesn't recognise this certificate, so the name above is only what the signer claims.", Secondary));
            }

            list.Children.Add(item);
        }

        if (checks.Count == 0)
        {
            list.Children.Add(Line("This PDF has no digital signatures. A signature drawn or typed on the page looks the part, but can't be checked."));
        }
        else
        {
            list.Children.Add(Line("Papery checks offline, so it can't tell if a certificate was cancelled later.", Secondary));
        }

        var dialog = new ContentDialog { Title = "Digital signatures", CloseButtonText = "Done", Content = new ScrollViewer { Content = list, MaxHeight = 440 } };
        await Dialogs.ShowAsync(dialog, root);
    }

    private static (string Glyph, string Text) Headline(PdfSignatureCheck check)
    {
        if (!check.CanCheck)
        {
            return ("", "Papery can't check this type of signature");
        }

        if (!check.Intact)
        {
            return ("", "Broken: this was changed after it was signed");
        }

        return check.CoversWholeFile
            ? ("", "Valid: nothing has changed since it was signed")
            : ("", "The signed part is intact, but more was added afterwards");
    }
}
