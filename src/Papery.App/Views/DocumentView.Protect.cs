using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Convert;
using Papery.Views.Fill;
using Papery.Views.Protect;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Papery.Views;

/// <summary>Protect mode: passwords, redaction, hidden data, document details and certified signatures.</summary>
public sealed partial class DocumentView
{
    private const int ProtectModeIndex = 6;
    private const int ToolsModeIndex = 7;

    private readonly List<RedactionArea> _redactions = [];
    private readonly List<(Canvas Layer, Rectangle Mark)> _redactionMarks = [];

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w.])\+?\(?\d[\d ()\-.]{7,}\d(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    private void EnterProtectMode()
    {
        ProtectToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
        ProtectMark.IsChecked = false;
        UpdateRedactionButtons();
        ShowRedactionMarks();
    }

    private void LeaveProtectMode()
    {
        ProtectToolbar.Visibility = Visibility.Collapsed;
        ClearRedactionMarks();
    }

    private async Task<bool> AllowProtectAsync(string key) => XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool(key));

    // ================================================================= redaction

    private void OnRedactMark(object sender, RoutedEventArgs e) => Guard.Run(ArmRedactMarkAsync, "Marking");

    private async Task ArmRedactMarkAsync()
    {
        if (ProtectMark.IsChecked != true)
        {
            Viewer.Tool = null;
            return;
        }

        if (!await AllowProtectAsync("redact"))
        {
            ProtectMark.IsChecked = false;
            return;
        }

        Toaster.Show("Drag over what to remove. Click a box to take it back", "");
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            var size = _document!.PageSizes[page];
            var box = new NormalizedRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            if (box.Width * size.Width >= 3 && box.Height * size.Height >= 3)
            {
                _redactions.Add(new RedactionArea(page, box));
            }
            else
            {
                _redactions.RemoveAll(r => r.PageIndex == page && start.X >= r.Area.X && start.X <= r.Area.Right && start.Y >= r.Area.Y && start.Y <= r.Area.Bottom);
            }

            UpdateRedactionButtons();
            ShowRedactionMarks();
        });
    }

    private void OnRedactFind(object sender, RoutedEventArgs e) => Guard.Run(FindRedactionsAsync, "Finding");

    private async Task FindRedactionsAsync()
    {
        if (_document is not { } document || !await AllowProtectAsync("redact") || await ProtectDialogs.AskRedactSearchAsync(XamlRoot) is not { } search)
        {
            return;
        }

        var patterns = new List<Regex>();
        if (search.Emails)
        {
            patterns.Add(EmailPattern());
        }

        if (search.Phones)
        {
            patterns.Add(PhonePattern());
        }

        if (search.Words.Length > 0)
        {
            patterns.Add(new Regex(Regex.Escape(search.Words), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        if (patterns.Count == 0)
        {
            return;
        }

        int count = 0;
        var found = new List<RedactionArea>();
        bool finished = await BusyDialog.RunAsync(XamlRoot, "Looking through the pages", async (progress, cancel) =>
        {
            for (int page = 0; page < document.PageCount; page++)
            {
                cancel.ThrowIfCancellationRequested();
                string text = await document.GetPageTextAsync(page, PdfWorkPriority.Background, cancel);
                foreach (var pattern in patterns)
                {
                    foreach (Match match in pattern.Matches(text))
                    {
                        var selection = await document.GetSelectionAsync(page, match.Index, match.Index + match.Length - 1, cancel);
                        found.AddRange(selection.Areas.Select(a => new RedactionArea(page, Outset(a))));
                        count++;
                    }
                }

                progress.Report((double)(page + 1) / document.PageCount);
            }
        });

        if (!finished)
        {
            return;
        }

        _redactions.AddRange(found);
        UpdateRedactionButtons();
        ShowRedactionMarks();
        Toaster.Show(count == 0 ? "Nothing like that was found" : count == 1 ? "1 place marked" : $"{count:N0} places marked", count == 0 ? "" : "");
    }

    /// <summary>A little extra around found words, so no edge of a letter is left showing.</summary>
    private static NormalizedRect Outset(NormalizedRect a)
    {
        const double Pad = 0.002;
        return new NormalizedRect(Math.Max(0, a.X - Pad), Math.Max(0, a.Y - Pad), a.Width + (2 * Pad), a.Height + (2 * Pad));
    }

    private void UpdateRedactionButtons()
    {
        ProtectApply.IsEnabled = _redactions.Count > 0;
        ProtectClear.Visibility = _redactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRedactClear(object sender, RoutedEventArgs e)
    {
        _redactions.Clear();
        UpdateRedactionButtons();
        ShowRedactionMarks();
    }

    private void ShowRedactionMarks()
    {
        ClearRedactionMarks();
        if (_modeIndex != ProtectModeIndex && ProtectToolbar.Visibility != Visibility.Visible)
        {
            return;
        }

        foreach (var redaction in _redactions)
        {
            if (Viewer.ToolLayerFor(redaction.PageIndex) is { ActualWidth: > 0 } layer)
            {
                var mark = new Rectangle
                {
                    Width = redaction.Area.Width * layer.ActualWidth,
                    Height = redaction.Area.Height * layer.ActualHeight,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0, 0, 0)),
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(mark, redaction.Area.X * layer.ActualWidth);
                Canvas.SetTop(mark, redaction.Area.Y * layer.ActualHeight);
                layer.Children.Add(mark);
                _redactionMarks.Add((layer, mark));
            }
        }
    }

    private void ClearRedactionMarks()
    {
        foreach (var (layer, mark) in _redactionMarks)
        {
            layer.Children.Remove(mark);
        }

        _redactionMarks.Clear();
    }

    private void OnRedactApply(object sender, RoutedEventArgs e) => Guard.Run(ApplyRedactionsAsync, "Redacting");

    private async Task ApplyRedactionsAsync()
    {
        if (_document is not { } document || _redactions.Count == 0 || !await AllowProtectAsync("redact") || !await SaveAnnotationsFirstAsync("Redacting"))
        {
            return;
        }

        if (document.Password != null)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Remove the password first", "Redaction needs the password taken off first. You can add one again afterwards.");
            return;
        }

        int pages = _redactions.Select(r => r.PageIndex).Distinct().Count();
        var confirm = new ContentDialog
        {
            Title = "Remove this for good?",
            Content = Dialogs.Wrap($"What's under the marks on {(pages == 1 ? "1 page" : $"{pages:N0} pages")} will be permanently removed, and those pages become pictures with their other words still searchable. If the file has a form, its answers become permanent too. Your original stays as it is: this makes a new copy."),
            PrimaryButtonText = "Choose where to save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Dialogs.ShowAsync(confirm, XamlRoot) != ContentDialogResult.Primary
            || await Pickers.PickSavePdfAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath) + "-redacted", FilePath) is not { } output)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Choose a different name", "A redacted copy can't replace the file that's open. Save it under a new name, then delete the original if you need to.");
            return;
        }

        try
        {
            var areas = _redactions.ToArray();
            if (!await BusyDialog.RunAsync(XamlRoot, "Removing and checking", (_, cancel) => PdfEngine.Shared.RedactAsync(new PdfInputFile(FilePath), areas, output, cancel)))
            {
                Pickers.RemoveIfEmpty(output);
                return;
            }
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await EditErrors.ShowAsync(ex, XamlRoot);
            return;
        }

        _redactions.Clear();
        UpdateRedactionButtons();
        ShowRedactionMarks();
        NewPdfMade?.Invoke(this, output);
    }

    // ================================================================= passwords

    private void OnAddPassword(object sender, RoutedEventArgs e) => Guard.Run(AddPasswordAsync, "Adding a password");

    private async Task AddPasswordAsync()
    {
        if (_document == null || !await AllowProtectAsync("protect") || await ProtectDialogs.AskPasswordAsync(XamlRoot) is not { } protection)
        {
            return;
        }

        await ChangeFileAsync("Password added", "", input => PdfEngine.Shared.ProtectAsync(input, protection, FilePath), passwordAfter: protection.Password, changesPassword: true);
    }

    private void OnRemovePassword(object sender, RoutedEventArgs e) => Guard.Run(RemovePasswordAsync, "Removing the password");

    private async Task RemovePasswordAsync()
    {
        if (_document is not { } document || !await AllowProtectAsync("protect"))
        {
            return;
        }

        if (document.Password == null)
        {
            Toaster.Show("This file doesn't have a password", "");
            return;
        }

        await ChangeFileAsync("Password removed", "", input => PdfEngine.Shared.RemovePasswordAsync(input, FilePath), passwordAfter: null, changesPassword: true);
    }

    // ================================================================= hidden data and details

    private void OnHiddenData(object sender, RoutedEventArgs e) => Guard.Run(RemoveHiddenDataAsync, "Removing hidden data");

    private async Task RemoveHiddenDataAsync()
    {
        if (_document is not { } document || !await AllowProtectAsync("protect") || !await SaveAnnotationsFirstAsync("Removing hidden data"))
        {
            return;
        }

        var input = new PdfInputFile(FilePath, _document.Password);
        var found = await PdfEngine.Shared.InspectHiddenDataAsync(input);
        if (await ProtectDialogs.AskHiddenDataAsync(XamlRoot, found) is { } choice)
        {
            await ChangeFileAsync("Hidden data removed", "", i => PdfEngine.Shared.RemoveHiddenDataAsync(i, choice, FilePath));
        }
    }

    private void OnEditDetails(object sender, RoutedEventArgs e) => Guard.Run(EditDetailsAsync, "Changing details");

    private async Task EditDetailsAsync()
    {
        if (_document is not { } document || !await AllowProtectAsync("protect"))
        {
            return;
        }

        if (await ProtectDialogs.AskMetadataAsync(XamlRoot, await document.GetMetadataAsync()) is { } details)
        {
            await ChangeFileAsync("Details saved", "", input => PdfEngine.Shared.SetMetadataAsync(input, details, FilePath));
        }
    }

    // ================================================================= certified signature

    private void OnCertifiedSign(object sender, RoutedEventArgs e) => Guard.Run(CertifiedSignAsync, "Signing");

    private async Task CertifiedSignAsync()
    {
        if (_document is not { } document || !await AllowSignAsync() || !await SaveAnnotationsFirstAsync("Signing"))
        {
            return;
        }

        if (await ProtectDialogs.AskCertifiedSignatureAsync(XamlRoot, PdfEngine.Shared.SigningCertificates()) is { } request)
        {
            await ChangeFileAsync("Certified digital signature added", "", input => PdfEngine.Shared.SignAsync(input, request, FilePath));
        }
    }
}
