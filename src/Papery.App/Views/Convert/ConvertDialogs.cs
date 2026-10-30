using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Papery.Controls;
using Papery.Core.Pages;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Edit;
using Windows.Graphics.Imaging;

namespace Papery.Views.Convert;

/// <summary>A job with a progress bar and a Cancel button. Returns false if it was cancelled.</summary>
internal static class BusyDialog
{
    public static async Task<bool> RunAsync(XamlRoot root, string title, Func<IProgress<double>, CancellationToken, Task> work)
    {
        using var cancel = new CancellationTokenSource();
        var bar = new ProgressBar { Maximum = 1, Width = 340 };
        var dialog = new ContentDialog { Title = title, Content = bar, CloseButtonText = "Cancel" };
        var progress = new Progress<double>(value => bar.Value = value);
        bool finished = false;
        dialog.Closing += (_, e) =>
        {
            if (!finished)
            {
                cancel.Cancel(); // the job stops first; it closes the dialog when it has
                e.Cancel = true;
                dialog.CloseButtonText = string.Empty;
                bar.IsIndeterminate = true;
            }
        };

        var showing = Dialogs.ShowAsync(dialog, root);
        try
        {
            await Task.Run(() => work(progress, cancel.Token), cancel.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            finished = true;
            dialog.Hide();
            await showing;
        }
    }
}

internal sealed record PagesToImagesChoice(IReadOnlyList<int> Pages, double Dpi, bool Jpeg);

/// <summary>Which pages to save as pictures, and how sharp.</summary>
internal static class PagesToImagesDialog
{
    private static readonly double[] Resolutions = [72, 150, 300, 600];

    public static async Task<PagesToImagesChoice?> ShowAsync(XamlRoot root, int pageCount)
    {
        var format = new PillBar();
        format.AddItem("PNG", null, "png");
        format.AddItem("JPG", null, "jpg");
        format.SelectedIndex = 0;

        var sharpness = new ComboBox { Header = "Sharpness", MinWidth = 200 };
        sharpness.Items.Add("72 dpi (screen)");
        sharpness.Items.Add("150 dpi (good)");
        sharpness.Items.Add("300 dpi (print)");
        sharpness.Items.Add("600 dpi (very sharp, big files)");
        sharpness.SelectedIndex = 1;

        var range = new TextBox { Header = "Pages", PlaceholderText = "All, or like 1-3, 7" };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Width = 340 };
        panel.Children.Add(format);
        panel.Children.Add(sharpness);
        panel.Children.Add(range);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Save pages as pictures",
            Content = panel,
            PrimaryButtonText = "Choose a folder",
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
            ? new PagesToImagesChoice(pages, Resolutions[Math.Max(0, sharpness.SelectedIndex)], format.SelectedIndex == 1)
            : null;
    }
}

/// <summary>A picture waiting to become a page.</summary>
internal sealed class PictureRow(string path)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    public double WidthPoints { get; set; }

    public double HeightPoints { get; set; }

    public bool KeepsJpeg { get; set; }
}

/// <summary>Pictures in a chosen order, with page size, margins and orientation. Returns the finished PDF's path.</summary>
internal static class ImagesToPdfDialog
{
    public static async Task<string?> ShowAsync(XamlRoot root, nint hwnd, IReadOnlyList<string>? starting = null)
    {
        var rows = new ObservableCollection<PictureRow>();
        var list = new ListView { Height = 220, CanReorderItems = true, CanDragItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = "Name", ItemsSource = rows };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Pictures, in page order");
        var empty = new TextBlock { Text = "Add some pictures to begin. Drag them to change the order.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] };

        async Task AddAsync(IEnumerable<string> paths)
        {
            foreach (string path in paths.Where(ImageFiles.IsImage))
            {
                if (await ImageFiles.MeasureAsync(path) is { } size)
                {
                    rows.Add(new PictureRow(path) { WidthPoints = size.WidthPoints, HeightPoints = size.HeightPoints, KeepsJpeg = size.KeepsJpeg });
                }
            }

            empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var add = new Button { Content = "Add pictures", Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        add.Click += (_, _) => Guard.Run(async () => await AddAsync(await ImageFiles.PickManyAsync(hwnd)), "Adding pictures");
        var remove = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is PictureRow row)
            {
                rows.Remove(row);
            }

            empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(add);
        buttons.Children.Add(remove);

        var paper = new ComboBox { Header = "Page size", MinWidth = 140 };
        foreach (string name in new[] { "Same as picture", "A4", "Letter", "Legal" })
        {
            paper.Items.Add(name);
        }

        paper.SelectedIndex = 0;
        var orientation = new ComboBox { Header = "Orientation", MinWidth = 120 };
        foreach (string name in new[] { "Automatic", "Portrait", "Landscape" })
        {
            orientation.Items.Add(name);
        }

        orientation.SelectedIndex = 0;
        var margin = EditPieces.Number(0, 0, 200, "Margin (mm)", 110);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        options.Children.Add(paper);
        options.Children.Add(orientation);
        options.Children.Add(margin);

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(buttons);
        panel.Children.Add(empty);
        panel.Children.Add(list);
        panel.Children.Add(options);

        var dialog = new ContentDialog
        {
            Title = "Images to PDF",
            Content = panel,
            PrimaryButtonText = "Make PDF",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = rows.Count == 0;
        dialog.Loaded += (_, _) => Guard.Run(() => AddAsync(starting ?? []), "Adding pictures");

        if (await Dialogs.ShowAsync(dialog, root) != ContentDialogResult.Primary)
        {
            return null;
        }

        string name2 = rows.Count == 1 ? System.IO.Path.GetFileNameWithoutExtension(rows[0].Name) : "Pictures";
        if (await Pickers.PickSavePdfAsync(hwnd, name2) is not { } output)
        {
            return null;
        }

        var choice = new ImagePageOptions((ImagePaperSize)Math.Max(0, paper.SelectedIndex), (ImageOrientation)Math.Max(0, orientation.SelectedIndex), (double.IsNaN(margin.Value) ? 0 : margin.Value) * 72 / 25.4);
        var pages = rows.Select(row => new PdfImagePage(row.WidthPoints, row.HeightPoints, () => ImageFiles.LoadForPageAsync(row.Path, row.KeepsJpeg))).ToArray();
        try
        {
            if (!await BusyDialog.RunAsync(root, "Making your PDF", (progress, cancel) => PdfEngine.Shared.ImagesToPdfAsync(pages, choice, output, progress, cancel)))
            {
                Pickers.RemoveIfEmpty(output);
                return null;
            }
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await EditErrors.ShowAsync(ex, root);
            return null;
        }

        return output;
    }
}

/// <summary>Getting any other app's work into a PDF, using what Windows already has.</summary>
internal static class PrintToPdfHelp
{
    public static Task ShowAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = "Make a PDF from any app",
            Content = Dialogs.Wrap("Windows can already do this. In the app you're using, choose Print, pick the printer called \"Microsoft Print to PDF\", and choose where to save. Then open that file in Papery."),
            CloseButtonText = "Got it",
        };
        return Dialogs.ShowAsync(dialog, root);
    }
}
