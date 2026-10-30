using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Convert;
using Papery.Views.Edit;
using Papery.Views.Fill;
using Papery.Views.Tools;

namespace Papery.Views;

/// <summary>Tools mode (compress, compare, batch), Word export, changing existing text, and the safety net around saving.</summary>
public sealed partial class DocumentView
{
    private const int AutosaveSeconds = 60;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autosave;
    private string? _recoveryCopy;
    private bool _overwriteOk;

    /// <summary>The user asked to compare this file with another.</summary>
    internal event EventHandler<string>? CompareRequested;

    /// <summary>The user asked for the Batch tool.</summary>
    internal event EventHandler? BatchRequested;

    /// <summary>The tab now shows a different file, so its title should change.</summary>
    internal event EventHandler? FileChanged;

    private void EnterToolsMode()
    {
        ToolsToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
    }

    private void LeaveToolsMode() => ToolsToolbar.Visibility = Visibility.Collapsed;

    // ================================================================= compress

    private void OnCompress(object sender, RoutedEventArgs e) => Guard.Run(CompressAsync, "Compressing");

    private async Task CompressAsync()
    {
        if (_document is not { } document || XamlRoot == null || !await ProDialogs.RequireProAsync(XamlRoot, Tool("compress")) || !await SaveAnnotationsFirstAsync("Compressing"))
        {
            return;
        }

        document = _document!;
        if (await CompressDialog.ShowAsync(XamlRoot, document, FilePath) is not { } outcome)
        {
            return;
        }

        string done = $"Compressed: {CompressDialog.Size(outcome.Before)} → {CompressDialog.Size(outcome.After)}";
        try
        {
            if (outcome.SaveElsewhere)
            {
                if (await Pickers.PickSavePdfAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath) + "-small", FilePath) is { } output)
                {
                    await PdfEngine.Shared.RestoreCopyAsync(outcome.SmallerCopy, document.Password, output);
                    Toaster.Show(done, "", "Open", () => NewPdfMade?.Invoke(this, output));
                }
            }
            else
            {
                await ChangeFileAsync(done, "", input => PdfEngine.Shared.RestoreCopyAsync(outcome.SmallerCopy, input.Password, FilePath));
            }
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
        }
        finally
        {
            CompressDialog.PdfCleanup(Path.GetDirectoryName(outcome.SmallerCopy)!);
        }
    }

    // ================================================================= compare and batch

    private void OnCompare(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        if (XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool("compare")) && (await Pickers.PickPdfsAsync(GetWindowHandle?.Invoke() ?? 0, multiple: false)) is { Count: 1 } other)
        {
            CompareRequested?.Invoke(this, other[0]);
        }
    }, "Comparing");

    private void OnBatch(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        if (XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool("batch")))
        {
            BatchRequested?.Invoke(this, EventArgs.Empty);
        }
    }, "Batch");

    // ================================================================= Word

    private void OnExportWord(object sender, RoutedEventArgs e) => Guard.Run(ExportWordAsync, "Saving as Word");

    private async Task ExportWordAsync()
    {
        if (_document is not { } document || !await AllowConvertAsync("pdf-to-images")
            || await Pickers.PickSaveAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath), "Word document", ".docx") is not { } output)
        {
            return;
        }

        bool done = await BusyDialog.RunAsync(XamlRoot, "Making a Word document", (progress, cancel) => PdfEngine.Shared.ExportWordAsync(document, output, progress, cancel));
        if (done)
        {
            Toaster.Show("Saved. Tables and columns may need tidying", "", "Show", () => Shell.ShowInFolder(output));
        }
        else
        {
            Pickers.RemoveIfEmpty(output);
        }
    }

    // ================================================================= changing existing text

    private void OnEditExisting(object sender, RoutedEventArgs e) => Guard.Run(ArmEditExistingAsync, "Changing text");

    private async Task ArmEditExistingAsync()
    {
        if (!await AllowEditAsync())
        {
            PickEditTool(EditSelect);
            return;
        }

        PickEditTool(EditExisting);
        Toaster.Show("Click a line of text to change it", "");
        Viewer.Tool = new ClickPlacer((page, start, _) =>
        {
            PickEditTool(EditSelect);
            Guard.Run(() => ChangeLineAsync(page, start), "Changing text");
        });
    }

    /// <summary>
    /// Fonts in PDFs usually hold only the letters they already use, so old words can't be retyped in place
    /// reliably. Instead the line is covered and new words go on top in the nearest font, which is honest and always works.
    /// </summary>
    private async Task ChangeLineAsync(int page, PagePoint point)
    {
        if (_document is not { } document || XamlRoot == null)
        {
            return;
        }

        int index = await document.GetCharIndexAtAsync(page, point.X, point.Y);
        if (index < 0)
        {
            Toaster.Show("No text there. A scan is only a picture of words", "");
            return;
        }

        var line = await document.GetLineAtAsync(page, index);
        if (line.Count == 0)
        {
            return;
        }

        var first = line[0];
        var font = first.FontName.Contains("Times", StringComparison.OrdinalIgnoreCase) || first.FontName.Contains("Serif", StringComparison.OrdinalIgnoreCase) || first.FontName.Contains("Georgia", StringComparison.OrdinalIgnoreCase) || first.FontName.Contains("Garamond", StringComparison.OrdinalIgnoreCase)
            ? PdfTextFont.Serif
            : first.FontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || first.FontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) || first.FontName.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                ? PdfTextFont.Mono
                : PdfTextFont.Sans;
        double left = line.Min(w => w.Area.X), top = line.Min(w => w.Area.Y), right = line.Max(w => w.Area.Right), bottom = line.Max(w => w.Area.Bottom);
        var size = document.PageSizes[page];
        const double Pad = 1.5;
        var cover = new NormalizedRect(left - (Pad / size.Width), top - (Pad / size.Height), right - left + (2 * Pad / size.Width), bottom - top + (2 * Pad / size.Height));
        var draft = new PdfAnnotationDraft(
            PdfAnnotationKind.TextBox,
            first.Color with { A = 255 },
            Text: string.Join(' ', line.Select(w => w.Text)),
            FontSize: Math.Max(4, first.FontSize),
            Font: font,
            Bold: first.Bold,
            Italic: first.Italic);
        if (await TextDialog.ShowAsync(XamlRoot, draft) is not { } chosen)
        {
            return;
        }

        var paper = await PaperColourAsync(document, page, cover);
        double newHeight = Math.Max(cover.Height, TextDialog.HeightFor(chosen.Text!, chosen.FontSize, Math.Max(cover.Width * size.Width, 60)) / size.Height);
        var covered = new PdfAnnotationDraft(PdfAnnotationKind.Whiteout, paper, [cover]);
        var words = chosen with { Areas = [new NormalizedRect(left - (4 / size.Width), Math.Max(0, top - (4 / size.Height)), Math.Min(Math.Max(right - left + (12 / size.Width), 0.2), 1 - left), newHeight + (6 / size.Height))] };
        var a = await document.AddAnnotationAsync(page, covered);
        var b = await document.AddAnnotationAsync(page, words);
        await AfterAnnotationChangeAsync(page);
        Remember(
            async () =>
            {
                await document.SetAnnotationHiddenAsync(page, a.Name, true);
                await document.SetAnnotationHiddenAsync(page, b.Name, true);
                await AfterAnnotationChangeAsync(page);
            },
            async () =>
            {
                await document.SetAnnotationHiddenAsync(page, a.Name, false);
                await document.SetAnnotationHiddenAsync(page, b.Name, false);
                await AfterAnnotationChangeAsync(page);
            });
        Toaster.Show("Changed. The old words are covered, not removed: use Redact to remove them", "", seconds: 7);
    }

    /// <summary>The colour just beside the words, so tinted paper doesn't get a white patch.</summary>
    private static async Task<PdfColor> PaperColourAsync(PdfDocument document, int page, NormalizedRect near)
    {
        var size = document.PageSizes[page];
        int w = Math.Max(1, (int)size.Width), h = Math.Max(1, (int)size.Height);
        var bitmap = await document.RenderPageAsync(page, w, h, PdfRenderOptions.None);
        int x = Math.Clamp((int)(near.X * w) - 3, 0, w - 1), y = Math.Clamp((int)((near.Y + (near.Height / 2)) * h), 0, h - 1);
        int at = (y * bitmap.Stride) + (x * 4);
        return new PdfColor(bitmap.Pixels[at + 2], bitmap.Pixels[at + 1], bitmap.Pixels[at]);
    }

    // ================================================================= asking before overwriting

    /// <summary>
    /// The first change that would touch the original asks first. Returns false to stop; "save a copy"
    /// moves this tab onto the copy, so the original stays as it was.
    /// </summary>
    private async Task<bool> ConfirmChangingOriginalAsync()
    {
        if (_overwriteOk || XamlRoot == null)
        {
            return true;
        }

        if (AppServices.Settings.Current.AlwaysSaveCopy)
        {
            return await MoveToCopyAsync();
        }

        var always = new CheckBox { Content = "Always save a copy" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Dialogs.Wrap($"This changes \"{FileName}\" itself. You can undo changes until you close this tab."));
        panel.Children.Add(always);
        var dialog = new ContentDialog
        {
            Title = "Change the original?",
            Content = panel,
            PrimaryButtonText = "Change it",
            SecondaryButtonText = "Save a copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var answer = await Dialogs.ShowAsync(dialog, XamlRoot);
        if (answer == ContentDialogResult.None)
        {
            return false;
        }

        if (always.IsChecked == true)
        {
            AppServices.Settings.Update(s => s.AlwaysSaveCopy = true);
        }

        if (answer == ContentDialogResult.Secondary || always.IsChecked == true)
        {
            return await MoveToCopyAsync();
        }

        _overwriteOk = true;
        return true;
    }

    private void OnSaveAs(object sender, RoutedEventArgs e) => Guard.Run(MoveToCopyAsync, "Saving a copy");

    /// <summary>Save as: writes everything so far to a new file and carries on working there.</summary>
    private async Task<bool> MoveToCopyAsync()
    {
        if (_document is not { } document
            || await Pickers.PickSavePdfAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath) + " copy", FilePath) is not { } output)
        {
            return false;
        }

        try
        {
            if (_formOverlay != null && XamlRoot != null)
            {
                await _formOverlay.FlushAsync(XamlRoot);
            }

            await document.SaveAsync(output);
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await EditErrors.ShowAsync(ex, XamlRoot);
            return false;
        }

        FilePath = Path.GetFullPath(output);
        _overwriteOk = true;
        _annotationUndo.Clear();
        _annotationRedo.Clear();
        _annotationCache.Clear();
        FileChanged?.Invoke(this, EventArgs.Empty);
        await ReloadAsync();
        UpdateAnnotateButtons();
        Toaster.Show("Saved as a copy. You're now working on the copy", "");
        return true;
    }

    // ================================================================= autosave for crashes

    private void StartAutosave()
    {
        if (_autosave != null)
        {
            return;
        }

        _autosave = DispatcherQueue.CreateTimer();
        _autosave.Interval = TimeSpan.FromSeconds(AutosaveSeconds);
        _autosave.IsRepeating = true;
        _autosave.Tick += (_, _) => Guard.Run(AutosaveAsync, "Autosave");
        _autosave.Start();
    }

    private async Task AutosaveAsync()
    {
        if (_document is not { HasAnnotationChanges: true } document || _closingTab)
        {
            return;
        }

        _recoveryCopy ??= RecoveryService.NewCopyPath();
        try
        {
            await document.SaveRecoveryCopyAsync(_recoveryCopy);
            RecoveryService.Describe(_recoveryCopy, FilePath);
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // It's a safety net, so a failure here shouldn't interrupt anyone. The next tick tries again.
            Log.Info($"Autosave skipped: {ex.GetType().Name}");
        }
    }

    private void ForgetRecoveryCopy()
    {
        RecoveryService.Forget(_recoveryCopy);
        _recoveryCopy = null;
    }
}
