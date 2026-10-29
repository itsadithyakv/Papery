using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Controls;
using Papery.Core.Pages;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views;

/// <summary>What a finished split produced.</summary>
public sealed record SplitOutcome(string Folder, IReadOnlyList<string> Files, bool Stopped);

/// <summary>
/// Split one PDF into several: every N pages, typed ranges, by chapter (bookmarks) or every page.
/// A live preview shows the files before anything is written; the original is never changed.
/// </summary>
internal static class SplitDialog
{
    private const int PreviewRows = 6;

    public static async Task<SplitOutcome?> ShowAsync(PdfDocument document, XamlRoot root, nint hwnd)
    {
        var outline = await document.GetOutlineAsync();
        int pageCount = document.PageCount;
        bool hasChapters = SplitPlanner.ByBookmarks(outline, pageCount).Count > 1;
        string folder = Path.GetDirectoryName(document.FilePath)!;
        string fileName = Path.GetFileName(document.FilePath);

        // ---- layout: method pills, the method's one setting, a preview, where it goes ----
        var panel = new StackPanel { Spacing = 14, Width = 460 };
        var methods = new PillBar();
        methods.AddItem("Every", "", "every", "Every few pages");
        methods.AddItem("Ranges", "", "ranges", "Page ranges");
        if (hasChapters)
        {
            methods.AddItem("Chapters", "", "chapters", "By chapter (bookmarks)");
        }

        methods.AddItem("Size", "", "size", "By file size, for email limits");
        methods.AddItem("Each page", "", "each", "Every page on its own");
        panel.Children.Add(methods);

        var everyBox = new NumberBox
        {
            Minimum = 1,
            Maximum = Math.Max(1, pageCount),
            Value = pageCount > 10 ? 10 : Math.Max(1, pageCount / 2),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 140,
        };
        var everyRow = Row("pages per file", everyBox);
        var rangesBox = new TextBox { PlaceholderText = "1-3, 4-10, 11-", Width = 260 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rangesBox, "Page ranges, one file per range");
        var rangesRow = Row("one file each", rangesBox);
        var sizeBox = new NumberBox
        {
            Minimum = 1,
            Maximum = 4000,
            Value = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 140,
        };
        var sizeRow = Row("MB at most per file", sizeBox);
        var settings = new Grid { MinHeight = 34 };
        settings.Children.Add(everyRow);
        settings.Children.Add(rangesRow);
        settings.Children.Add(sizeRow);
        panel.Children.Add(settings);

        var summary = new TextBlock { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var preview = new StackPanel { Spacing = 4 };
        var previewCard = new Border { Style = (Style)Application.Current.Resources["PaperyCardStyle"], Padding = new Thickness(14, 10, 14, 12) };
        var previewStack = new StackPanel { Spacing = 8 };
        previewStack.Children.Add(summary);
        previewStack.Children.Add(preview);
        previewCard.Child = previewStack;
        panel.Children.Add(previewCard);

        var folderText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330 };
        var changeFolder = new Button { Content = "Change", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        var folderRow = new Grid { ColumnSpacing = 10 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(new FontIcon { Glyph = "", FontSize = 16, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["PaperyIcons"] });
        Grid.SetColumn(folderText, 1);
        folderRow.Children.Add(folderText);
        Grid.SetColumn(changeFolder, 2);
        folderRow.Children.Add(changeFolder);
        panel.Children.Add(folderRow);

        var progress = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            Title = "Split",
            Content = panel,
            PrimaryButtonText = "Split",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // ---- live preview ----
        IReadOnlyList<SplitPart> parts = [];
        bool bySize = false;
        long SizeLimit() => (long)(Math.Clamp(double.IsNaN(sizeBox.Value) ? 10 : sizeBox.Value, 1, 4000) * 1024 * 1024);
        void Refresh()
        {
            string method = (string)methods.Items[Math.Max(0, methods.SelectedIndex)].Tag;
            everyRow.Visibility = method == "every" ? Visibility.Visible : Visibility.Collapsed;
            rangesRow.Visibility = method == "ranges" ? Visibility.Visible : Visibility.Collapsed;
            sizeRow.Visibility = method == "size" ? Visibility.Visible : Visibility.Collapsed;
            folderText.Text = folder;
            ToolTipService.SetToolTip(folderText, folder);
            preview.Children.Clear();
            if (method == "size")
            {
                // Pages share fonts and images, so a page's share of the file can't be known in advance:
                // the parts are found while splitting. The estimate is only for the preview.
                bySize = true;
                parts = [];
                long limit = SizeLimit();
                long estimate = Math.Max(1, (document.FileSize + limit - 1) / limit);
                summary.Text = estimate == 1 ? "Probably 1 file" : $"About {estimate:N0} files";
                preview.Children.Add(Secondary($"Each file stays under {sizeBox.Value:N0} MB. Papery works out where to cut while splitting."));
                dialog.IsPrimaryButtonEnabled = !double.IsNaN(sizeBox.Value);
                dialog.PrimaryButtonText = "Split";
                return;
            }

            bySize = false;
            string? error = null;
            parts = method switch
            {
                "every" => SplitPlanner.EveryNPages(pageCount, Math.Clamp(double.IsNaN(everyBox.Value) ? 1 : (int)everyBox.Value, 1, Math.Max(1, pageCount))),
                "ranges" => PageRanges.TryParseGroups(rangesBox.Text, pageCount, out var groups, out error) ? SplitPlanner.FromGroups(groups) : [],
                "chapters" => SplitPlanner.ByBookmarks(outline, pageCount),
                _ => SplitPlanner.EveryNPages(pageCount, 1),
            };

            if (parts.Count == 0)
            {
                summary.Text = "No files yet";
                preview.Children.Add(Secondary(string.IsNullOrWhiteSpace(rangesBox.Text) ? "Type the pages for each file, separated by commas." : error ?? string.Empty));
            }
            else
            {
                summary.Text = parts.Count == 1 ? "1 file" : $"{parts.Count:N0} files";
                foreach (var part in parts.Take(PreviewRows))
                {
                    preview.Children.Add(PartRow(SplitPlanner.FileName(fileName, part), part.Pages));
                }

                if (parts.Count > PreviewRows)
                {
                    preview.Children.Add(Secondary($"and {parts.Count - PreviewRows:N0} more"));
                }
            }

            dialog.IsPrimaryButtonEnabled = parts.Count > 0;
            dialog.PrimaryButtonText = parts.Count > 1 ? $"Split into {parts.Count:N0} files" : "Split";
        }

        methods.SelectionChanged += (_, _) => Refresh();
        everyBox.ValueChanged += (_, _) => Refresh();
        rangesBox.TextChanged += (_, _) => Refresh();
        sizeBox.ValueChanged += (_, _) => Refresh();
        changeFolder.Click += async (_, _) =>
        {
            if (await Pickers.PickFolderAsync(hwnd) is { } picked)
            {
                folder = picked;
                Refresh();
            }
        };
        methods.SelectedIndex = 0;
        Refresh();

        // ---- the split itself, inside the dialog so progress and Cancel stay in one place ----
        SplitOutcome? outcome = null;
        CancellationTokenSource? running = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // stay open while working
            var deferral = args.GetDeferral();
            running = new CancellationTokenSource();
            dialog.IsPrimaryButtonEnabled = false;
            methods.IsHitTestVisible = settings.IsHitTestVisible = false;
            changeFolder.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            var written = new List<string>();
            try
            {
                var source = new PdfInputFile(document.FilePath, document.Password);
                PdfComposeRequest Request(IReadOnlyList<int> pages)
                {
                    var map = pages.Select((page, index) => (page, index)).GroupBy(x => x.page).ToDictionary(g => g.Key, g => g.First().index);
                    return new PdfComposeRequest(
                        [source],
                        pages.Select(p => new PageRef(0, p)).ToArray(),
                        Outline: SplitPlanner.RemapOutline(outline, p => map.TryGetValue(p, out int at) ? at : null));
                }

                if (bySize)
                {
                    await SizeSplitter.SplitAsync(
                        PdfEngine.Shared,
                        pageCount,
                        SizeLimit(),
                        document.FileSize,
                        Request,
                        part => SplitPlanner.UniquePath(Path.Combine(folder, SplitPlanner.FileName(fileName, part))),
                        written,
                        new Progress<double>(v => progress.Value = v),
                        running.Token);
                }

                for (int i = 0; i < parts.Count && !bySize; i++)
                {
                    var part = parts[i];
                    string path = SplitPlanner.UniquePath(Path.Combine(folder, SplitPlanner.FileName(fileName, part)));
                    int done = i;
                    var step = new Progress<double>(v => progress.Value = (done + v) / parts.Count);
                    await PdfEngine.Shared.ComposeAsync(Request(part.Pages), path, step, running.Token);
                    written.Add(path);
                }

                outcome = new SplitOutcome(folder, written, Stopped: false);
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
                outcome = written.Count > 0 ? new SplitOutcome(folder, written, Stopped: true) : null;
                dialog.Hide();
            }
            catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
            {
                dialog.Hide();
                outcome = written.Count > 0 ? new SplitOutcome(folder, written, Stopped: true) : null;
                await EditErrors.ShowAsync(ex, root);
            }
            finally
            {
                running = null;
                deferral.Complete();
            }
        };

        // While splitting, Cancel stops after the current file instead of closing straight away.
        dialog.CloseButtonClick += (_, args) =>
        {
            if (running != null)
            {
                running.Cancel();
                args.Cancel = true;
            }
        };

        await Dialogs.ShowAsync(dialog, root);
        return outcome;
    }

    /// <summary>The toast message after a split.</summary>
    public static string Describe(SplitOutcome outcome) => outcome.Stopped
        ? $"Stopped. {outcome.Files.Count:N0} files were saved"
        : outcome.Files.Count == 1 ? "Saved 1 file" : $"Split into {outcome.Files.Count:N0} files";

    private static StackPanel Row(string label, FrameworkElement input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(input);
        row.Children.Add(Secondary(label));
        return row;
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
    };

    private static Grid PartRow(string name, IReadOnlyList<int> pages)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["PaperyIcons"] });
        var nameText = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);
        string count = pages.Count == 1 ? "1 page" : $"{pages.Count.ToString("N0", CultureInfo.CurrentCulture)} pages";
        var countText = Secondary(count);
        countText.FontSize = 12;
        Grid.SetColumn(countText, 2);
        row.Children.Add(countText);
        return row;
    }
}
