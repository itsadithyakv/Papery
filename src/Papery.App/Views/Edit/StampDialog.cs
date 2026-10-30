using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Core.Pages;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Edit;

internal enum StampKind
{
    PageNumbers,
    HeaderFooter,
    Watermark,
    Bates,
}

/// <summary>Sets up page numbers, a header or footer, a watermark or Bates numbers, with a live preview.</summary>
internal static class StampDialog
{
    private const double PreviewWidth = 250;

    private static readonly (string Label, string Template)[] NumberFormats =
    [
        ("1", "{page}"),
        ("Page 1", "Page {page}"),
        ("Page 1 of 10", "Page {page} of {pages}"),
        ("1 / 10", "{page} / {pages}"),
    ];

    private static readonly PageStampPosition[,] PositionGrid =
    {
        { PageStampPosition.TopLeft, PageStampPosition.TopCentre, PageStampPosition.TopRight },
        { PageStampPosition.Middle, PageStampPosition.Middle, PageStampPosition.Middle },
        { PageStampPosition.BottomLeft, PageStampPosition.BottomCentre, PageStampPosition.BottomRight },
    };

    public static async Task<PdfPageStamp?> ShowAsync(XamlRoot root, nint hwnd, StampKind kind, PdfDocument document)
    {
        bool numbered = kind is StampKind.PageNumbers or StampKind.Bates;
        var stamp = new PdfPageStamp
        {
            Position = kind switch
            {
                StampKind.Watermark => PageStampPosition.Middle,
                StampKind.Bates => PageStampPosition.BottomRight,
                StampKind.HeaderFooter => PageStampPosition.TopCentre,
                _ => PageStampPosition.BottomCentre,
            },
            FontSize = kind == StampKind.Watermark ? 64 : 11,
            Color = kind == StampKind.Watermark ? new PdfColor(110, 110, 110) : new PdfColor(33, 33, 33),
            Opacity = kind == StampKind.Watermark ? 0.3 : 1,
            AngleDegrees = kind == StampKind.Watermark ? 45 : 0,
            Text = kind == StampKind.Watermark ? "DRAFT" : null,
            Bold = kind == StampKind.Watermark,
        };
        PdfBitmap? picture = null;
        Action refresh = () => { };

        // ---- preview: the first page, with the stamp drawn the way the page will show it ----
        var size = document.PageSizes[0];
        double scale = PreviewWidth / size.Width;
        var pageImage = new Image { Width = PreviewWidth, Height = size.Height * scale, Stretch = Stretch.Fill };
        var previewText = new TextBlock { IsHitTestVisible = false };
        var previewPicture = new Image { IsHitTestVisible = false, Stretch = Stretch.Uniform };
        var overlay = new Canvas { Width = PreviewWidth, Height = size.Height * scale };
        overlay.Children.Add(previewText);
        overlay.Children.Add(previewPicture);
        var pagePreview = new Grid { Width = PreviewWidth, Height = size.Height * scale, Background = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Top };
        pagePreview.Children.Add(pageImage);
        pagePreview.Children.Add(overlay);
        Guard.Run(async () =>
        {
            var bitmap = await document.RenderPageAsync(0, (int)(PreviewWidth * 1.5), (int)(size.Height * scale * 1.5), PdfRenderOptions.Annotations, PdfWorkPriority.Visible);
            pageImage.Source = PageBitmaps.ToWriteableBitmap(bitmap);
        }, "Showing a preview");

        // ---- the words ----
        var form = new StackPanel { Spacing = 12, Width = 340 };
        var formats = new ComboBox { Header = "Style", MinWidth = 180 };
        foreach (var (label, _) in NumberFormats)
        {
            formats.Items.Add(label);
        }

        formats.SelectedIndex = 2;
        var words = new TextBox { Header = kind == StampKind.Watermark ? "Words" : "Text", Text = stamp.Text ?? string.Empty };
        var pageToken = new Button { Content = "Page number", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        var dateToken = new Button { Content = "Date", Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"] };
        var tokens = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tokens.Children.Add(pageToken);
        tokens.Children.Add(dateToken);

        var prefix = new TextBox { Header = "Before", Width = 110 };
        var suffix = new TextBox { Header = "After", Width = 110 };
        var digits = EditPieces.Number(6, 1, 12, "Digits", 90);
        var batesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        batesRow.Children.Add(prefix);
        batesRow.Children.Add(digits);
        batesRow.Children.Add(suffix);

        var choosePicture = new Button { Content = "Choose a picture", Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        var useKinds = new PillBar();
        useKinds.AddItem("Words", "", "words");
        useKinds.AddItem("Picture", "", "picture");
        useKinds.SelectedIndex = 0;

        switch (kind)
        {
            case StampKind.PageNumbers:
                form.Children.Add(formats);
                break;
            case StampKind.HeaderFooter:
                form.Children.Add(words);
                form.Children.Add(tokens);
                break;
            case StampKind.Bates:
                form.Children.Add(batesRow);
                break;
            default:
                form.Children.Add(useKinds);
                form.Children.Add(words);
                form.Children.Add(choosePicture);
                break;
        }

        // ---- the look ----
        var font = EditPieces.FontPicker(PdfTextFont.Sans);
        font.Header = "Font";
        var fontSize = EditPieces.Number(stamp.FontSize, 4, 300, "Size");
        var bold = EditPieces.StyleToggle("B", stamp.Bold, Microsoft.UI.Text.FontWeights.Bold, name: "Bold");
        var italic = EditPieces.StyleToggle("I", false, style: Windows.UI.Text.FontStyle.Italic, name: "Italic");
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        marks.Children.Add(bold);
        marks.Children.Add(italic);
        var lookRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lookRow.Children.Add(font);
        lookRow.Children.Add(fontSize);
        lookRow.Children.Add(marks);
        form.Children.Add(lookRow);

        var colourRow = EditPieces.ColourRow(EditPieces.InkColours, stamp.Color, c => { stamp = stamp with { Color = c }; refresh(); });
        form.Children.Add(colourRow);

        // ---- how see-through and how turned (watermarks) ----
        var opacity = new Slider { Header = "Strength", Minimum = 5, Maximum = 100, Value = stamp.Opacity * 100, StepFrequency = 5 };
        var angle = new Slider { Header = "Angle", Minimum = -90, Maximum = 90, Value = stamp.AngleDegrees, StepFrequency = 15 };
        if (kind == StampKind.Watermark)
        {
            var sliders = new Grid { ColumnSpacing = 12 };
            sliders.ColumnDefinitions.Add(new ColumnDefinition());
            sliders.ColumnDefinitions.Add(new ColumnDefinition());
            sliders.Children.Add(opacity);
            Grid.SetColumn(angle, 1);
            sliders.Children.Add(angle);
            form.Children.Add(sliders);
        }

        // ---- where ----
        var positions = new Grid { RowSpacing = 2, ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        var positionButtons = new List<(RadioButton Button, PageStampPosition Position)>();
        for (int row = 0; row < 3; row++)
        {
            positions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int column = 0; column < 3; column++)
        {
            positions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                if (row == 1 && column != 1)
                {
                    continue; // the middle row has just the one spot
                }

                var at = PositionGrid[row, column];
                var dot = new RadioButton
                {
                    GroupName = "stampPlace",
                    Style = (Style)Application.Current.Resources["PaperyPlaceDotStyle"],
                    IsChecked = at == stamp.Position,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, at.ToString());
                dot.Checked += (_, _) => { stamp = stamp with { Position = at }; refresh(); };
                Grid.SetRow(dot, row);
                Grid.SetColumn(dot, column);
                positions.Children.Add(dot);
                positionButtons.Add((dot, at));
            }
        }

        var whereRow = new Grid { ColumnSpacing = 20 };
        whereRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        whereRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        whereRow.Children.Add(positions);

        var start = EditPieces.Number(1, 0, 99_999_999, "Start at", 120);
        var skipFirst = new CheckBox { Content = "Skip the first page" };
        var range = new TextBox { Header = "Pages", PlaceholderText = "All, or like 1-3, 7" };
        var error = new TextBlock { Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var options = new StackPanel { Spacing = 8 };
        if (numbered)
        {
            options.Children.Add(start);
        }

        if (kind != StampKind.Watermark)
        {
            options.Children.Add(skipFirst);
        }

        Grid.SetColumn(options, 1);
        whereRow.Children.Add(options);
        form.Children.Add(whereRow);
        form.Children.Add(range);
        form.Children.Add(error);

        // ---- keeping the preview true ----
        string TextNow() => kind switch
        {
            StampKind.PageNumbers => NumberFormats[Math.Max(0, formats.SelectedIndex)].Template,
            StampKind.Bates => $"{prefix.Text}{{page:{(int)Math.Clamp(double.IsNaN(digits.Value) ? 6 : digits.Value, 1, 12)}}}{suffix.Text}",
            StampKind.Watermark when useKinds.SelectedIndex == 1 => string.Empty,
            _ => words.Text,
        };

        void Refresh()
        {
            bool usePicture = kind == StampKind.Watermark && useKinds.SelectedIndex == 1;
            words.Visibility = kind == StampKind.HeaderFooter || (kind == StampKind.Watermark && !usePicture) ? Visibility.Visible : Visibility.Collapsed;
            choosePicture.Visibility = usePicture ? Visibility.Visible : Visibility.Collapsed;
            lookRow.Visibility = usePicture ? Visibility.Collapsed : Visibility.Visible;
            colourRow.Visibility = usePicture ? Visibility.Collapsed : Visibility.Visible;
            double points = double.IsNaN(fontSize.Value) ? 11 : fontSize.Value;
            double turn = kind == StampKind.Watermark ? angle.Value : 0;
            double strength = kind == StampKind.Watermark ? opacity.Value / 100 : 1;
            int first = (int)(double.IsNaN(start.Value) ? 1 : start.Value);

            previewText.Text = usePicture ? string.Empty : PageStampPlan.Resolve(TextNow(), first, first + document.PageCount - 1, DateTime.Now);
            previewText.FontFamily = EditPieces.FamilyFor((PdfTextFont)Math.Max(0, font.SelectedIndex));
            previewText.FontSize = Math.Max(1, points * scale);
            previewText.FontWeight = bold.IsChecked == true ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            previewText.FontStyle = italic.IsChecked == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            previewText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, stamp.Color.R, stamp.Color.G, stamp.Color.B));
            previewText.Opacity = strength;
            previewPicture.Opacity = strength;

            previewText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = previewText.DesiredSize.Width, h = previewText.DesiredSize.Height;
            var (across, down) = PageStampPlan.Centre(stamp.Position, size.Width, size.Height, w / scale, points, stamp.Margin);
            Canvas.SetLeft(previewText, (across * scale) - (w / 2));
            Canvas.SetTop(previewText, (down * scale) - (h / 2));
            previewText.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            previewText.RenderTransform = new RotateTransform { Angle = -turn };

            if (usePicture && picture != null)
            {
                const double PictureWidth = 300;
                double pw = PictureWidth * scale, ph = pw * picture.Height / picture.Width;
                var (pa, pd) = PageStampPlan.Centre(stamp.Position, size.Width, size.Height, PictureWidth, PictureWidth * picture.Height / picture.Width, stamp.Margin);
                previewPicture.Width = pw;
                previewPicture.Height = ph;
                Canvas.SetLeft(previewPicture, (pa * scale) - (pw / 2));
                Canvas.SetTop(previewPicture, (pd * scale) - (ph / 2));
                previewPicture.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                previewPicture.RenderTransform = new RotateTransform { Angle = -turn };
                previewPicture.Visibility = Visibility.Visible;
            }
            else
            {
                previewPicture.Visibility = Visibility.Collapsed;
            }
        }

        refresh = Refresh;
        words.TextChanged += (_, _) => Refresh();
        prefix.TextChanged += (_, _) => Refresh();
        suffix.TextChanged += (_, _) => Refresh();
        digits.ValueChanged += (_, _) => Refresh();
        start.ValueChanged += (_, _) => Refresh();
        formats.SelectionChanged += (_, _) => Refresh();
        font.SelectionChanged += (_, _) => Refresh();
        fontSize.ValueChanged += (_, _) => Refresh();
        bold.Click += (_, _) => Refresh();
        italic.Click += (_, _) => Refresh();
        opacity.ValueChanged += (_, _) => Refresh();
        angle.ValueChanged += (_, _) => Refresh();
        useKinds.SelectionChanged += (_, _) => Refresh();
        void Insert(string token)
        {
            int at = words.SelectionStart;
            words.Text = words.Text.Insert(at, token);
            words.SelectionStart = at + token.Length;
        }

        pageToken.Click += (_, _) => Insert("{page}");
        dateToken.Click += (_, _) => Insert("{date}");
        choosePicture.Click += (_, _) => Guard.Run(async () =>
        {
            if (await ImageFiles.PickAsync(hwnd) is { } path && await ImageFiles.LoadAsync(path, 1200) is { } loaded)
            {
                picture = loaded;
                previewPicture.Source = PageBitmaps.ToWriteableBitmap(loaded);
                Refresh();
            }
        }, "Choosing a picture");

        var scroller = new ScrollViewer { Content = form, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 16, 0) };
        var layout = new Grid { ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(scroller);
        Grid.SetColumn(pagePreview, 1);
        layout.Children.Add(pagePreview);

        var dialog = new ContentDialog
        {
            Title = kind switch
            {
                StampKind.PageNumbers => "Page numbers",
                StampKind.HeaderFooter => "Header or footer",
                StampKind.Bates => "Bates numbers",
                _ => "Watermark",
            },
            Content = layout,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        dialog.Loaded += (_, _) => Refresh();

        IReadOnlyList<int>? pages = null;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            error.Visibility = Visibility.Collapsed;
            pages = null;
            if (!string.IsNullOrWhiteSpace(range.Text) && !PageRanges.TryParse(range.Text, document.PageCount, out pages, out string? problem))
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
                return;
            }

            bool usePicture = kind == StampKind.Watermark && useKinds.SelectedIndex == 1;
            if (usePicture ? picture == null : string.IsNullOrWhiteSpace(TextNow()))
            {
                error.Text = usePicture ? "Choose a picture first." : "Type something to add first.";
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };

        if (await Dialogs.ShowAsync(dialog, root) != ContentDialogResult.Primary)
        {
            return null;
        }

        bool asPicture = kind == StampKind.Watermark && useKinds.SelectedIndex == 1;
        return stamp with
        {
            Text = asPicture ? null : TextNow(),
            Picture = asPicture ? picture : null,
            PictureWidth = 300,
            Font = (PdfTextFont)Math.Max(0, font.SelectedIndex),
            FontSize = (float)(double.IsNaN(fontSize.Value) ? 11 : fontSize.Value),
            Bold = bold.IsChecked == true,
            Italic = italic.IsChecked == true,
            Opacity = kind == StampKind.Watermark ? opacity.Value / 100 : 1,
            AngleDegrees = kind == StampKind.Watermark ? angle.Value : 0,
            Pages = pages,
            StartNumber = (int)(double.IsNaN(start.Value) ? 1 : start.Value),
            SkipFirst = kind != StampKind.Watermark && skipFirst.IsChecked == true,
        };
    }
}
