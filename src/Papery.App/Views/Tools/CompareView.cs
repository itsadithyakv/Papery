using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Text;
using Papery.Pdf;
using Papery.Services;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Papery.Views.Tools;

/// <summary>Two versions side by side, page by page, with changed words marked: red for gone, green for new.</summary>
internal sealed partial class CompareView : Grid
{
    private const double PageWidth = 620;

    private readonly PdfDocument _left;
    private readonly PdfDocument _right;
    private readonly Image _leftImage = new() { Stretch = Stretch.Fill };
    private readonly Image _rightImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _leftMarks = new() { IsHitTestVisible = false };
    private readonly Canvas _rightMarks = new() { IsHitTestVisible = false };
    private readonly Grid _leftPage = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _rightPage = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _where = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous;
    private readonly Button _next;
    private int _page;
    private int _showing;

    public CompareView(PdfDocument left, PdfDocument right)
    {
        _left = left;
        _right = right;
        Padding = new Thickness(16);
        RowSpacing = 12;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _previous = Tool("", "Previous page", () => Go(-1));
        _next = Tool("", "Next page", () => Go(1));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(_previous);
        bar.Children.Add(_next);
        bar.Children.Add(_where);
        bar.Children.Add(new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_summary);
        Children.Add(bar);

        var sides = new Grid { ColumnSpacing = 12 };
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        sides.ColumnDefinitions.Add(new ColumnDefinition());
        var leftSide = Side(Path.GetFileName(left.FilePath), _leftPage, _leftImage, _leftMarks);
        var rightSide = Side(Path.GetFileName(right.FilePath), _rightPage, _rightImage, _rightMarks);
        SetColumn(rightSide, 1);
        sides.Children.Add(leftSide);
        sides.Children.Add(rightSide);
        SetRow(sides, 1);
        Children.Add(sides);

        Guard.Run(ShowAsync, "Comparing");
    }

    private static Grid Side(string name, Grid page, Image image, Canvas marks)
    {
        page.Children.Add(image);
        page.Children.Add(marks);
        var side = new Grid { RowSpacing = 6 };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] });
        var scroller = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, ZoomMode = ZoomMode.Enabled };
        SetRow(scroller, 1);
        side.Children.Add(scroller);
        return side;
    }

    private static Button Tool(string glyph, string name, Action act)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, Style = (Style)Application.Current.Resources["ToolIconStyle"] },
            Style = (Style)Application.Current.Resources["ToolButtonStyle"],
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => act();
        return button;
    }

    private int LastPage => Math.Max(_left.PageCount, _right.PageCount) - 1;

    private void Go(int by)
    {
        _page = Math.Clamp(_page + by, 0, LastPage);
        Guard.Run(ShowAsync, "Comparing");
    }

    private async Task ShowAsync()
    {
        int page = _page;
        int ticket = ++_showing;
        _where.Text = $"Page {page + 1:N0} of {LastPage + 1:N0}";
        _previous.IsEnabled = page > 0;
        _next.IsEnabled = page < LastPage;
        var leftWords = page < _left.PageCount ? await _left.GetPageWordsAsync(page, PdfWorkPriority.Visible) : [];
        var rightWords = page < _right.PageCount ? await _right.GetPageWordsAsync(page, PdfWorkPriority.Visible) : [];
        var (removed, added) = await Task.Run(() => WordDiff.Compare(leftWords.Select(w => w.Text).ToArray(), rightWords.Select(w => w.Text).ToArray()));
        await ShowSideAsync(_left, page, _leftPage, _leftImage, _leftMarks, removed.Select(i => leftWords[i].Area), Windows.UI.Color.FromArgb(0x60, 0xE5, 0x39, 0x35), ticket);
        await ShowSideAsync(_right, page, _rightPage, _rightImage, _rightMarks, added.Select(i => rightWords[i].Area), Windows.UI.Color.FromArgb(0x60, 0x4C, 0xAF, 0x50), ticket);
        if (ticket == _showing)
        {
            int changes = removed.Count + added.Count;
            _summary.Text = changes == 0 ? "No text differences on this page" : $"{removed.Count:N0} removed, {added.Count:N0} added";
        }
    }

    private async Task ShowSideAsync(PdfDocument document, int page, Grid frame, Image image, Canvas marks, IEnumerable<NormalizedRect> areas, Windows.UI.Color colour, int ticket)
    {
        marks.Children.Clear();
        if (page >= document.PageCount)
        {
            image.Source = null;
            frame.Visibility = Visibility.Collapsed;
            return;
        }

        frame.Visibility = Visibility.Visible;
        var size = document.PageSizes[page];
        double height = PageWidth * size.Height / size.Width;
        frame.Width = PageWidth;
        frame.Height = height;
        var bitmap = await document.RenderPageAsync(page, (int)(PageWidth * 1.5), (int)(height * 1.5));
        if (ticket != _showing)
        {
            return;
        }

        image.Source = PageBitmaps.ToWriteableBitmap(bitmap);
        var brush = new SolidColorBrush(colour);
        foreach (var area in areas)
        {
            var mark = new Rectangle { Width = area.Width * PageWidth, Height = area.Height * height, Fill = brush };
            Canvas.SetLeft(mark, area.X * PageWidth);
            Canvas.SetTop(mark, area.Y * height);
            marks.Children.Add(mark);
        }
    }

    public async Task CloseAsync()
    {
        await _left.DisposeAsync();
        await _right.DisposeAsync();
    }
}
