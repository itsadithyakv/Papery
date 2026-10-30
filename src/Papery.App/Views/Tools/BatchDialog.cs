using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Tools;

/// <summary>One file in a batch, and how it's getting on.</summary>
public sealed partial class BatchRow(string path) : ObservableObject
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);

    [ObservableProperty]
    public partial string Status { get; set; } = "Waiting";
}

/// <summary>One action over many files. Originals are never overwritten: results go in a folder or get "-edited" on their name.</summary>
internal static class BatchDialog
{
    private enum BatchAction
    {
        Compress,
        ImagesToPdf,
        Watermark,
        PageNumbers,
        Rotate,
        Ocr,
        AddPassword,
        RemovePassword,
    }

    private static readonly string[] ActionNames =
    [
        "Compress", "Turn images into PDFs", "Add a watermark", "Add page numbers", "Rotate pages", "Make searchable", "Add a password", "Remove a password",
    ];

    public static async Task ShowAsync(XamlRoot root, nint hwnd, IReadOnlyList<string> starting)
    {
        var rows = new ObservableCollection<BatchRow>();
        void Add(IEnumerable<string> paths)
        {
            foreach (string path in paths.Where(p => rows.All(r => !string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add(new BatchRow(path));
            }
        }

        Add(starting);
        var list = new ListView { Height = 200, SelectionMode = ListViewSelectionMode.None, ItemsSource = rows, ItemTemplate = RowTemplate() };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Files");

        var addPdfs = new Button { Content = "Add PDFs", Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        addPdfs.Click += (_, _) => Guard.Run(async () => Add(await Pickers.PickPdfsAsync(hwnd)), "Adding files");
        var addImages = new Button { Content = "Add images", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        addImages.Click += (_, _) => Guard.Run(async () => Add(await ImageFiles.PickManyAsync(hwnd)), "Adding files");
        var clear = new Button { Content = "Clear", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        clear.Click += (_, _) => rows.Clear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(addPdfs);
        buttons.Children.Add(addImages);
        buttons.Children.Add(clear);

        var action = new ComboBox { Header = "Do this to each file", MinWidth = 240 };
        foreach (string name in ActionNames)
        {
            action.Items.Add(name);
        }

        action.SelectedIndex = 0;

        // What each action needs to know.
        var words = new TextBox { Header = "Watermark words", Text = "DRAFT", Width = 200 };
        var preset = new ComboBox { Header = "Strength", MinWidth = 150 };
        foreach (string name in new[] { "High quality", "Balanced", "Smallest" })
        {
            preset.Items.Add(name);
        }

        preset.SelectedIndex = 1;
        var turn = new ComboBox { Header = "Turn", MinWidth = 150 };
        foreach (string name in new[] { "Right (90°)", "Upside down (180°)", "Left (90°)" })
        {
            turn.Items.Add(name);
        }

        turn.SelectedIndex = 0;
        var password = new PasswordBox { Header = "Password", Width = 200 };
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        details.Children.Add(words);
        details.Children.Add(preset);
        details.Children.Add(turn);
        details.Children.Add(password);

        void ShowDetails()
        {
            var chosen = (BatchAction)Math.Max(0, action.SelectedIndex);
            words.Visibility = chosen == BatchAction.Watermark ? Visibility.Visible : Visibility.Collapsed;
            preset.Visibility = chosen == BatchAction.Compress ? Visibility.Visible : Visibility.Collapsed;
            turn.Visibility = chosen == BatchAction.Rotate ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = chosen is BatchAction.AddPassword or BatchAction.RemovePassword ? Visibility.Visible : Visibility.Collapsed;
        }

        action.SelectionChanged += (_, _) => ShowDetails();
        ShowDetails();

        var nextTo = new RadioButton { Content = "Next to each file, with \"-edited\" on its name", GroupName = "batchOut", IsChecked = true };
        var folder = new RadioButton { Content = "In a folder I choose", GroupName = "batchOut" };
        var panel = new StackPanel { Spacing = 10, Width = 500 };
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        panel.Children.Add(action);
        panel.Children.Add(details);
        panel.Children.Add(nextTo);
        panel.Children.Add(folder);

        var dialog = new ContentDialog { Title = "Batch", Content = panel, PrimaryButtonText = "Start", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary };
        dialog.Resources["ContentDialogMaxWidth"] = 600.0;

        CancellationTokenSource? running = null;
        dialog.Closing += (_, _) => running?.Cancel();
        dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true; // the dialog stays open to show how each file went
            if (running != null || rows.Count == 0)
            {
                return;
            }

            var chosen = (BatchAction)Math.Max(0, action.SelectedIndex);
            string pass = password.Password;
            int strength = preset.SelectedIndex, turns = turn.SelectedIndex + 1;
            string text = words.Text;
            bool askFolder = folder.IsChecked == true;
            Guard.Run(async () =>
            {
                string? outputFolder = askFolder ? await Pickers.PickFolderAsync(hwnd) : null;
                if (askFolder && outputFolder == null)
                {
                    return;
                }

                using var cancel = running = new CancellationTokenSource();
                dialog.IsPrimaryButtonEnabled = false;
                foreach (var row in rows)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        row.Status = "Stopped";
                        continue;
                    }

                    row.Status = "Working…";
                    try
                    {
                        row.Status = await RunOneAsync(chosen, row.Path, OutputFor(row.Path, outputFolder), text, strength, turns, pass, cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        row.Status = "Stopped";
                    }
                    catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired)
                    {
                        row.Status = "Needs a password";
                    }
                    catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Log.Error("A batch file failed", ex);
                        row.Status = "Couldn't do this one";
                    }
                }

                running = null;
                dialog.IsPrimaryButtonEnabled = true;
            }, "Batch");
        };

        await Dialogs.ShowAsync(dialog, root);
    }

    private static string OutputFor(string input, string? folder)
    {
        string stem = Path.GetFileNameWithoutExtension(input);
        string where = folder ?? Path.GetDirectoryName(input)!;
        string path = Path.Combine(where, $"{stem}-edited.pdf");
        for (int copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(where, $"{stem}-edited ({copy}).pdf");
        }

        return path;
    }

    private static async Task<string> RunOneAsync(BatchAction action, string input, string output, string words, int strength, int turns, string password, CancellationToken cancel)
    {
        var engine = PdfEngine.Shared;
        if (action == BatchAction.ImagesToPdf)
        {
            if (!ImageFiles.IsImage(input))
            {
                return "Not an image: skipped";
            }

            if (await ImageFiles.MeasureAsync(input) is not { } size)
            {
                return "Couldn't read this picture";
            }

            await engine.ImagesToPdfAsync([new PdfImagePage(size.WidthPoints, size.HeightPoints, () => ImageFiles.LoadForPageAsync(input, size.KeepsJpeg))], new ImagePageOptions(), output, null, cancel);
            return "Done";
        }

        if (!DropFiles.IsPdf(input))
        {
            return "Not a PDF: skipped";
        }

        var file = new PdfInputFile(input, action == BatchAction.RemovePassword ? password : null);
        switch (action)
        {
            case BatchAction.Compress:
                var options = strength == 0 ? CompressOptions.HighQuality : strength == 2 ? CompressOptions.Smallest : CompressOptions.Balanced;
                var result = await engine.CompressAsync(file, options, output, cancel);
                return result.Changed ? $"{CompressDialog.Size(result.OriginalBytes)} → {CompressDialog.Size(result.NewBytes)}" : "Already small: no new file";
            case BatchAction.Watermark:
                await engine.StampPagesAsync(file, new PdfPageStamp { Text = words, Position = PageStampPosition.Middle, FontSize = 64, Bold = true, Color = new PdfColor(110, 110, 110), Opacity = 0.3, AngleDegrees = 45 }, output, cancel);
                return "Done";
            case BatchAction.PageNumbers:
                await engine.StampPagesAsync(file, new PdfPageStamp { Text = "Page {page} of {pages}" }, output, cancel);
                return "Done";
            case BatchAction.Rotate:
                await RotateAsync(file, turns, output, cancel);
                return "Done";
            case BatchAction.Ocr:
                return await MakeSearchableAsync(file, output, cancel);
            case BatchAction.AddPassword:
                if (password.Length == 0)
                {
                    return "Type a password first";
                }

                await engine.ProtectAsync(file, new PdfProtection(password), output, cancel);
                return "Done";
            default:
                await engine.RemovePasswordAsync(file, output, cancel);
                return "Done";
        }
    }

    private static async Task RotateAsync(PdfInputFile file, int turns, string output, CancellationToken cancel)
    {
        await using var document = await PdfEngine.Shared.OpenAsync(file.Path, file.Password, cancel);
        var pages = Enumerable.Range(0, document.PageCount).Select(i => new PageRef(0, i, turns)).ToArray();
        await PdfEngine.Shared.ComposeAsync(new PdfComposeRequest([file], pages, EditInPlaceOf: 0), output, null, cancel);
    }

    private static async Task<string> MakeSearchableAsync(PdfInputFile file, string output, CancellationToken cancel)
    {
        if (OcrService.DefaultLanguage is not { } language)
        {
            return "No reading language installed";
        }

        await using var document = await PdfEngine.Shared.OpenAsync(file.Path, file.Password, cancel);
        var found = new List<OcrPage>();
        for (int page = 0; page < document.PageCount; page++)
        {
            var read = await OcrService.ReadPageAsync(document, page, language, cancel);
            if (read.Words.Count > 0)
            {
                found.Add(read);
            }
        }

        if (found.Count == 0)
        {
            return "Already searchable: no new file";
        }

        await PdfEngine.Shared.AddTextLayerAsync(file, found, output, cancel);
        return found.Count == 1 ? "1 page read" : $"{found.Count:N0} pages read";
    }

    private static DataTemplate RowTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid ColumnSpacing="12">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                <TextBlock Grid.Column="1" Text="{Binding Status}" VerticalAlignment="Center" Opacity="0.7" />
            </Grid>
        </DataTemplate>
        """);
}
