using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Tools;

/// <summary>What the filters dialog decided.</summary>
/// <param name="Pages">The pages to change, or null for all of them.</param>
internal sealed record FilterChoice(PictureFilterOptions Options, IReadOnlyCollection<int>? Pages, bool SaveElsewhere);

/// <summary>Looks and sliders for a PDF's pictures, with the page before and after. The preview is drawn by the same code that does the real work.</summary>
internal static class FilterDialog
{
    private const double PreviewWidth = 250;
    private const int SliderPause = 140;

    private static readonly (string Name, PictureLook Look)[] Looks =
    [
        ("Clean document", PictureLook.Document),
        ("Auto", PictureLook.Auto),
        ("Grey", PictureLook.Grey),
        ("Black and white", PictureLook.BlackAndWhite),
        ("Vivid", PictureLook.Vivid),
        ("Original", PictureLook.Original),
    ];

    public static async Task<FilterChoice?> ShowAsync(XamlRoot root, PdfDocument document, string filePath, int currentPage)
    {
        var looks = new PillBar();
        foreach (var (name, _) in Looks)
        {
            looks.AddItem(name, null, name);
        }

        var secondary = (Style)Application.Current.Resources["PaperySecondaryTextStyle"];
        Slider Dial(string name, int min) => new() { Minimum = min, Maximum = 100, Value = 0, StepFrequency = 1, Width = 150, Header = name };
        var brightness = Dial("Brightness", -100);
        var contrast = Dial("Contrast", -100);
        var sharpen = Dial("Sharpen", 0);
        var smooth = Dial("Remove grain", 0);
        var straighten = new CheckBox { Content = "Straighten crooked scans" };
        var scope = new PillBar();
        scope.AddItem("All pages", null, "all");
        scope.AddItem("This page", null, "this");

        var dials = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 165, MaximumRowsOrColumns = 2 };
        dials.Children.Add(brightness);
        dials.Children.Add(contrast);
        dials.Children.Add(sharpen);
        dials.Children.Add(smooth);

        var left = new StackPanel { Spacing = 12, Width = 340 };
        left.Children.Add(dials);
        left.Children.Add(straighten);
        left.Children.Add(scope);

        Image Page() => new() { Width = PreviewWidth, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var before = Page();
        var after = Page();
        StackPanel Labelled(string label, Image image)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = label, Style = secondary });
            stack.Children.Add(image);
            return stack;
        }

        var pictures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pictures.Children.Add(Labelled("Before", before));
        pictures.Children.Add(Labelled("After", after));
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = secondary };

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        body.Children.Add(left);
        body.Children.Add(new ScrollViewer { Content = pictures, MaxHeight = 400, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(looks);
        panel.Children.Add(body);
        panel.Children.Add(note);

        var dialog = new ContentDialog
        {
            Title = "Filters",
            Content = panel,
            PrimaryButtonText = "Apply",
            SecondaryButtonText = "Save a copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;

        var input = new PdfInputFile(filePath, document.Password);
        int width = (int)(PreviewWidth * 1.5);
        var size = document.PageSizes[currentPage];
        int height = Math.Max(1, (int)(width * size.Height / size.Width));

        PictureFilterOptions Options() => new(
            Looks[Math.Max(0, looks.SelectedIndex)].Look,
            (int)brightness.Value,
            (int)contrast.Value,
            (int)sharpen.Value,
            (int)smooth.Value,
            straighten.IsChecked == true);

        CancellationTokenSource? running = null;
        async Task PreviewAsync()
        {
            running?.Cancel();
            var cancel = running = new CancellationTokenSource();
            try
            {
                await Task.Delay(SliderPause, cancel.Token);
                var options = Options();
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = !options.DoesNothing;
                var page = await PdfEngine.Shared.PreviewPictureFilterAsync(input, currentPage, options, width, height, cancel.Token);
                if (!cancel.IsCancellationRequested)
                {
                    after.Source = PageBitmaps.ToWriteableBitmap(page);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer choice took over.
            }
            catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Filter preview failed", ex);
                note.Text = "Papery couldn't show this page.";
            }
        }

        void Refresh() => Guard.Run(PreviewAsync, "Showing a preview");
        looks.SelectionChanged += (_, _) => Refresh();
        foreach (var slider in new[] { brightness, contrast, sharpen, smooth })
        {
            AutomationProperties.SetName(slider, (string)slider.Header);
            slider.ValueChanged += (_, _) => Refresh();
        }

        straighten.Click += (_, _) => Refresh();
        dialog.Loaded += (_, _) =>
        {
            scope.SelectedIndex = 0;
            looks.SelectedIndex = 0;
            Refresh(); // the first look may already count as chosen, which raises no change
        };
        Guard.Run(async () => before.Source = PageBitmaps.ToWriteableBitmap(await document.RenderPageAsync(currentPage, width, height)), "Showing a preview");

        var answer = await Dialogs.ShowAsync(dialog, root);
        running?.Cancel();
        if (answer == ContentDialogResult.None)
        {
            return null;
        }

        return new FilterChoice(Options(), scope.SelectedIndex == 1 ? [currentPage] : null, answer == ContentDialogResult.Secondary);
    }
}
