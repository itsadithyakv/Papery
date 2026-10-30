using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Edit;

namespace Papery.Views.Tools;

/// <summary>What the compress dialog decided: a finished smaller copy, and whether it replaces the file or is saved elsewhere.</summary>
internal sealed record CompressOutcome(string SmallerCopy, long Before, long After, bool SaveElsewhere);

/// <summary>Presets with a true before and after: each choice really compresses a private copy, so the size shown is the real one.</summary>
internal static class CompressDialog
{
    private const double PreviewWidth = 210;

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };

    public static async Task<CompressOutcome?> ShowAsync(XamlRoot root, PdfDocument document, string filePath)
    {
        var presets = new PillBar();
        presets.AddItem("High quality", null, "high");
        presets.AddItem("Balanced", null, "balanced");
        presets.AddItem("Smallest", null, "smallest");
        presets.AddItem("Fit under", null, "fit");

        var limit = EditPieces.Number(5, 0.1, 10_000, "MB", 110);
        limit.Visibility = Visibility.Collapsed;
        var grey = new CheckBox { Content = "Make pictures grey" };
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Bottom };
        options.Children.Add(limit);
        options.Children.Add(grey);

        var sizes = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] };
        var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };

        Image Page() => new() { Width = PreviewWidth, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var before = Page();
        var after = Page();
        StackPanel Labelled(string label, Image image)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] });
            stack.Children.Add(image);
            return stack;
        }

        var pictures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pictures.Children.Add(Labelled("Before", before));
        pictures.Children.Add(Labelled("After", after));

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(presets);
        panel.Children.Add(options);
        panel.Children.Add(sizes);
        panel.Children.Add(ring);
        panel.Children.Add(note);
        panel.Children.Add(new ScrollViewer { Content = pictures, MaxHeight = 320, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var dialog = new ContentDialog
        {
            Title = "Compress",
            Content = panel,
            PrimaryButtonText = "Replace this file",
            SecondaryButtonText = "Save a copy",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "compress-preview", Guid.NewGuid().ToString("N"))).FullName;
        var input = new PdfInputFile(filePath, document.Password);
        long original = new FileInfo(filePath).Length;
        CancellationTokenSource? running = null;
        string? ready = null;
        long readySize = 0;
        int preview = (int)(PreviewWidth * 1.5);
        int previewHeight = (int)(preview * document.PageSizes[0].Height / document.PageSizes[0].Width);

        Guard.Run(async () => before.Source = PageBitmaps.ToWriteableBitmap(await document.RenderPageAsync(0, preview, previewHeight)), "Showing a preview");

        async Task RunAsync()
        {
            running?.Cancel();
            var cancel = running = new CancellationTokenSource();
            ready = null;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            ring.IsActive = true;
            sizes.Text = $"{Size(original)} now";
            note.Text = string.Empty;
            after.Source = null;
            string output = Path.Combine(work, $"{Guid.NewGuid():N}.pdf");
            try
            {
                int chosen = Math.Max(0, presets.SelectedIndex);
                var result = chosen == 3
                    ? await PdfEngine.Shared.CompressToFitAsync(input, (long)((double.IsNaN(limit.Value) ? 5 : limit.Value) * 1024 * 1024), output, cancel.Token)
                    : await PdfEngine.Shared.CompressAsync(input, (chosen == 0 ? CompressOptions.HighQuality : chosen == 2 ? CompressOptions.Smallest : CompressOptions.Balanced) with { Greyscale = grey.IsChecked == true }, output, cancel.Token);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                if (!result.Changed)
                {
                    sizes.Text = $"{Size(original)}: already small";
                    note.Text = "Papery couldn't make this one any smaller without harming it.";
                    return;
                }

                ready = output;
                readySize = result.NewBytes;
                int saved = (int)Math.Round(100.0 * (original - result.NewBytes) / original);
                sizes.Text = $"{Size(original)} → {Size(result.NewBytes)}, {saved}% smaller";
                note.Text = result.FitsLimit ? string.Empty : "This is the smallest Papery can go, and it's still over your limit.";
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = true;
                await using var smaller = await PdfEngine.Shared.OpenAsync(output, document.Password, cancel.Token);
                var page = await smaller.RenderPageAsync(0, preview, previewHeight, cancellationToken: cancel.Token);
                after.Source = PageBitmaps.ToWriteableBitmap(page);
            }
            catch (OperationCanceledException)
            {
                // A newer choice took over.
            }
            catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Compress failed", ex);
                sizes.Text = "Papery couldn't compress this file";
                note.Text = "Your file has not been changed.";
            }
            finally
            {
                if (running == cancel)
                {
                    ring.IsActive = false;
                }
            }
        }

        void Start() => Guard.Run(RunAsync, "Compressing");

        presets.SelectionChanged += (_, _) =>
        {
            limit.Visibility = presets.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            grey.Visibility = presets.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
            Start();
        };
        limit.ValueChanged += (_, _) => Start();
        grey.Click += (_, _) => Start();
        dialog.Loaded += (_, _) => presets.SelectedIndex = 1;

        var answer = await Dialogs.ShowAsync(dialog, root);
        running?.Cancel();
        if (answer == ContentDialogResult.None || ready == null)
        {
            PdfCleanup(work);
            return null;
        }

        return new CompressOutcome(ready, original, readySize, answer == ContentDialogResult.Secondary);
    }

    public static void PdfCleanup(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't delete a preview folder: {ex.GetType().Name}");
        }
    }
}
