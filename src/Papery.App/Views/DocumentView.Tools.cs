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

    // ================================================================= locks on the toolbars

    // These work without Pro, or only show up once something has been picked.
    private static readonly HashSet<string> OpenToEveryone =
    [
        "Select, move or resize", "Change the words", "Turn", "Delete", "Snapshot", "Save as text", "Copy all the text",
        "Make a PDF from any app", "Clear the marks", "Document details", "Save a copy",
    ];

    private readonly List<(Microsoft.UI.Xaml.Controls.Primitives.ButtonBase Button, string Name, object? Tip)> _lockableButtons = [];

    /// <summary>Pro tools look a little quieter until unlocked, and Narrator says so.</summary>
    private void UpdateToolbarLocks(bool pro)
    {
        if (_lockableButtons.Count == 0)
        {
            foreach (var toolbar in new FrameworkElement[] { EditToolbar, ConvertToolbar, ProtectToolbar, ToolsToolbar })
            {
                foreach (var button in Buttons(toolbar))
                {
                    string name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button);
                    if (!string.IsNullOrEmpty(name) && !OpenToEveryone.Contains(name))
                    {
                        _lockableButtons.Add((button, name, ToolTipService.GetToolTip(button)));
                    }
                }
            }
        }

        foreach (var (button, name, tip) in _lockableButtons)
        {
            button.Opacity = pro ? 1 : 0.55;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, pro ? name : $"{name}, Pro feature");
            ToolTipService.SetToolTip(button, pro ? tip : $"{tip ?? name} (Papery Pro)");
        }

        static IEnumerable<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase> Buttons(object? element)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
            {
                return [button];
            }

            return element switch
            {
                Panel panel => panel.Children.SelectMany(Buttons),
                ScrollViewer scroller => Buttons(scroller.Content),
                Border border => Buttons(border.Child),
                _ => [],
            };
        }
    }

    // ================================================================= filters

    private void OnFilters(object sender, RoutedEventArgs e) => Guard.Run(FilterAsync, "Applying filters");

    private async Task FilterAsync()
    {
        if (_document is not { } document || XamlRoot == null || !await ProDialogs.RequireProAsync(XamlRoot, Tool("filters")) || !await SaveAnnotationsFirstAsync("Filtering"))
        {
            return;
        }

        document = _document!;
        if (await FilterDialog.ShowAsync(XamlRoot, document, FilePath, Math.Clamp(Viewer.CurrentPage, 0, document.PageCount - 1)) is not { } choice)
        {
            return;
        }

        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "filter-result", Guid.NewGuid().ToString("N"))).FullName;
        string made = Path.Combine(work, "filtered.pdf");
        try
        {
            PictureFilterResult? result = null;
            var input = new PdfInputFile(FilePath, document.Password);
            if (!await BusyDialog.RunAsync(XamlRoot, "Improving the pictures", async (progress, cancel) => result = await PdfEngine.Shared.FilterPicturesAsync(input, choice.Options, choice.Pages, made, progress, cancel)))
            {
                return;
            }

            if (result is not { Changed: > 0 })
            {
                await Dialogs.ShowMessageAsync(XamlRoot, "No pictures to change", result is { LeftAlone: > 0 }
                    ? "The pictures here are see-through or already pure black and white, so Papery left them as they are."
                    : "Filters change pictures and scans. These pages only have text and shapes.");
                return;
            }

            string done = result.Changed == 1 ? "1 picture improved" : $"{result.Changed:N0} pictures improved";
            if (result.LeftAlone > 0)
            {
                done += $", {result.LeftAlone:N0} left as they were";
            }

            if (!choice.SaveElsewhere)
            {
                await ChangeFileAsync(done, "\uE790", file => PdfEngine.Shared.RestoreCopyAsync(made, file.Password, FilePath));
            }
            else if (await Pickers.PickSavePdfAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath) + "-filtered", FilePath) is { } output)
            {
                await PdfEngine.Shared.RestoreCopyAsync(made, document.Password, output);
                Toaster.Show(done, "\uE790", "Open", () => NewPdfMade?.Invoke(this, output));
            }
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
        }
        finally
        {
            CompressDialog.PdfCleanup(work);
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
    /// Fonts in PDFs usually hold only the letters they already use. So when the look is unchanged the words are first tried in
    /// place, which really removes the old ones; otherwise the line is covered and new words go on top in the nearest font, which always works.
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

        bool sameLook = chosen.Font == draft.Font && chosen.Bold == draft.Bold && chosen.Italic == draft.Italic && Math.Abs(chosen.FontSize - draft.FontSize) < 0.01 && chosen.Color == draft.Color;
        if (sameLook && chosen.Text is { Length: > 0 } typed && !typed.Contains('\n', StringComparison.Ordinal) && !document.HasAnnotationChanges && await ReplaceInPlaceAsync(document, page, cover, draft.Text!, typed))
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

    private async Task<bool> ReplaceInPlaceAsync(PdfDocument document, int page, NormalizedRect line, string oldText, string newText)
    {
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "text-change", Guid.NewGuid().ToString("N"))).FullName;
        string made = Path.Combine(work, "changed.pdf");
        try
        {
            if (!await PdfEngine.Shared.TryReplaceLineAsync(new PdfInputFile(FilePath, document.Password), page, line, oldText, newText, made))
            {
                return false;
            }

            return await ChangeFileAsync("Changed", "", input => PdfEngine.Shared.RestoreCopyAsync(made, input.Password, FilePath));
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't change the words in place: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            CompressDialog.PdfCleanup(work);
        }
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
