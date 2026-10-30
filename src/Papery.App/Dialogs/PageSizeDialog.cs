using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Organise;

namespace Papery.Views;

/// <summary>What the page size dialog decided. A null paper keeps each page's size.</summary>
public sealed record PageSizeChoice(PdfPageSize? Paper, bool FitContent, float Margin);

/// <summary>
/// Paper size and margins for the chosen pages, with a live preview of the first one.
/// Each page gets the paper turned its own way, so landscape pages stay landscape.
/// </summary>
internal static class PageSizeDialog
{
    private static readonly (string Name, PdfPageSize? Size)[] Papers =
    [
        ("Keep each page's size", null),
        ("A4", PaperSizes.A4),
        ("A5", PaperSizes.A5),
        ("A3", PaperSizes.A3),
        ("US Letter", PaperSizes.Letter),
        ("US Legal", PaperSizes.Legal),
        ("Custom", null),
    ];

    private const int CustomIndex = 6;

    public static async Task<PageSizeChoice?> ShowAsync(XamlRoot root, OrganisePage sample, int pageCount)
    {
        var current = sample.Geometry;
        var paperBox = new ComboBox { MinWidth = 220, Header = "Paper" };
        foreach (var (name, _) in Papers)
        {
            paperBox.Items.Add(name);
        }

        paperBox.SelectedIndex = current?.Paper is { } p ? IndexOf(p) : 0;
        float mm = PaperSizes.PointsPerMillimetre;
        var widthBox = new NumberBox { Header = "Width (mm)", Minimum = 20, Maximum = 2000, Value = Math.Round((current?.Paper?.Width ?? PaperSizes.A4.Width) / mm), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130 };
        var heightBox = new NumberBox { Header = "Height (mm)", Minimum = 20, Maximum = 2000, Value = Math.Round((current?.Paper?.Height ?? PaperSizes.A4.Height) / mm), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130 };
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        customRow.Children.Add(widthBox);
        customRow.Children.Add(heightBox);
        var fitSwitch = new ToggleSwitch { Header = "Content", OnContent = "Scale to fill the paper", OffContent = "Keep its real size", IsOn = current?.FitContent ?? true };
        var marginBox = new NumberBox { Header = "Margins (mm)", Minimum = 0, Maximum = 100, Value = Math.Round((current?.Margin ?? 0) / mm), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130 };

        var settings = new StackPanel { Spacing = 14, Width = 270 };
        settings.Children.Add(paperBox);
        settings.Children.Add(customRow);
        settings.Children.Add(fitSwitch);
        settings.Children.Add(marginBox);

        // The preview is a real page tile showing the first page with the new settings.
        var preview = new OrganisePage(-1, sample.Reference, sample.Document, null, sample.Size, NoActions.Instance)
        {
            QuarterTurns = sample.QuarterTurns,
            Image = sample.Image,
            Paper = sample.Paper,
            TileSize = 190,
            Geometry = current,
        };
        var tile = new PageTile { DataContext = preview, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var sizeText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"], FontSize = 12 };
        var previewStack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        previewStack.Children.Add(tile);
        previewStack.Children.Add(sizeText);

        var layout = new Grid { ColumnSpacing = 24 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(settings);
        Grid.SetColumn(previewStack, 1);
        layout.Children.Add(previewStack);

        PageSizeChoice Choice()
        {
            PdfPageSize? paper = paperBox.SelectedIndex == CustomIndex
                ? new PdfPageSize((float)(Value(widthBox, 210) * mm), (float)(Value(heightBox, 297) * mm))
                : Papers[Math.Max(0, paperBox.SelectedIndex)].Size;
            return new PageSizeChoice(paper, fitSwitch.IsOn, (float)(Value(marginBox, 0) * mm));
        }

        void Refresh()
        {
            customRow.Visibility = paperBox.SelectedIndex == CustomIndex ? Visibility.Visible : Visibility.Collapsed;
            fitSwitch.Visibility = paperBox.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            preview.Geometry = Apply(current, Choice(), sample.TurnedSize);
            var result = (preview.Geometry ?? new PageGeometry()).Layout(sample.TurnedSize);
            sizeText.Text = $"{result.Paper.Width / mm:0} × {result.Paper.Height / mm:0} mm";
        }

        paperBox.SelectionChanged += (_, _) => Refresh();
        widthBox.ValueChanged += (_, _) => Refresh();
        heightBox.ValueChanged += (_, _) => Refresh();
        fitSwitch.Toggled += (_, _) => Refresh();
        marginBox.ValueChanged += (_, _) => Refresh();
        Refresh();

        var dialog = new ContentDialog
        {
            Title = pageCount == 1 ? "Page size and margins" : $"Page size and margins for {pageCount:N0} pages",
            Content = layout,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
        };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary ? Choice() : null;
    }

    /// <summary>
    /// A page's geometry with the chosen paper and margins: the paper is turned to match the page's
    /// shape (after any crop), so a landscape page goes on landscape paper.
    /// </summary>
    public static PageGeometry? Apply(PageGeometry? geometry, PageSizeChoice choice, PdfPageSize turnedPage)
    {
        var crop = geometry?.Crop ?? PageGeometry.Whole;
        var shape = new PdfPageSize((float)(turnedPage.Width * crop.Width), (float)(turnedPage.Height * crop.Height));
        var updated = (geometry ?? new PageGeometry()) with
        {
            Paper = choice.Paper is { } paper ? PaperSizes.Oriented(paper, shape) : null,
            FitContent = choice.FitContent,
            Margin = choice.Margin,
        };
        return updated.IsEmpty ? null : updated;
    }

    private static int IndexOf(PdfPageSize size)
    {
        for (int i = 1; i < CustomIndex; i++)
        {
            var known = Papers[i].Size!.Value;
            bool same = (Math.Abs(known.Width - size.Width) < 1 && Math.Abs(known.Height - size.Height) < 1)
                || (Math.Abs(known.Width - size.Height) < 1 && Math.Abs(known.Height - size.Width) < 1);
            if (same)
            {
                return i;
            }
        }

        return CustomIndex;
    }

    private static double Value(NumberBox box, double fallback) => double.IsNaN(box.Value) ? fallback : box.Value;

    /// <summary>The preview tile's buttons do nothing.</summary>
    private sealed class NoActions : IOrganiseActions
    {
        public static readonly NoActions Instance = new();

        public void RotatePage(OrganisePage page, int quarterTurns)
        {
        }

        public void DeletePage(OrganisePage page)
        {
        }
    }
}
