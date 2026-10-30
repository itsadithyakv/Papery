using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Papery.Pdf;
using Windows.UI;

namespace Papery.Controls;

/// <summary>
/// One page on screen: a sheet of "paper" with a soft shadow, the rendered image, optional
/// high-zoom tiles on top, and an overlay for search hits and text selection.
/// Instances are pooled and reused as you scroll.
/// </summary>
internal sealed partial class PageVisual : Grid
{
    private readonly Border _paper;
    private readonly Image _base;
    private readonly Canvas _tiles;
    private readonly Canvas _overlay;
    private readonly TextBlock _error;

    public PageVisual()
    {
        // Three faint offset outlines read as a soft shadow, without the cost of a real blur.
        foreach (var (spread, alpha) in new[] { (3.0, 10), (2.0, 16), (1.0, 26) })
        {
            Children.Add(new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb((byte)alpha, 0, 0, 0)),
                Margin = new Thickness(-spread, -spread + 1, -spread, -spread - 1),
                RadiusX = 2 + spread,
                RadiusY = 2 + spread,
                IsHitTestVisible = false,
            });
        }

        _base = new Image { Stretch = Stretch.Fill };
        _tiles = new Canvas { IsHitTestVisible = false };
        _overlay = new Canvas { IsHitTestVisible = false };
        _error = new TextBlock
        {
            Text = "This page couldn't be displayed.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.Gray),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        };

        var content = new Grid();
        content.Children.Add(_base);
        content.Children.Add(_tiles);
        content.Children.Add(_overlay);
        content.Children.Add(FormLayer);
        content.Children.Add(ToolLayer);
        content.Children.Add(_error);
        _paper = new Border { Child = content, Background = new SolidColorBrush(Colors.White), CornerRadius = new CornerRadius(1) };
        Children.Add(_paper);
        Background = new SolidColorBrush(Colors.Transparent); // receives pointer events everywhere
    }

    public int PageIndex { get; set; } = -1;

    /// <summary>The form field controls. Only its children catch clicks; the rest of the page stays selectable.</summary>
    public Canvas FormLayer { get; } = new();

    /// <summary>Live previews of what a tool is drawing. Clicks pass through to the page.</summary>
    public Canvas ToolLayer { get; } = new() { IsHitTestVisible = false };

    /// <summary>Pixel width of the image currently shown (0 = none yet).</summary>
    public int BaseWidth { get; private set; }

    /// <summary>Pixel width of the render in progress, to avoid requesting the same thing twice.</summary>
    public int PendingWidth { get; set; }

    public CancellationTokenSource RenderCancellation { get; private set; } = new();

    /// <summary>Tiles currently shown, keyed by (column, row) at <see cref="TileScale"/>.</summary>
    public Dictionary<(int Column, int Row), Image> Tiles { get; } = new();

    public double TileScale { get; set; }

    public void SetPaperColor(Color color) => ((SolidColorBrush)_paper.Background).Color = color;

    public void SetBase(WriteableBitmap? bitmap, int width)
    {
        _base.Source = bitmap;
        BaseWidth = width;
        _error.Visibility = Visibility.Collapsed;
    }

    public void ShowError() => _error.Visibility = Visibility.Visible;

    /// <summary>The shown image is out of date (e.g. reading mode changed): keep showing it until the new one arrives.</summary>
    public void InvalidateBase() => BaseWidth = -1;

    public void AddTile((int Column, int Row) key, Image image)
    {
        Tiles[key] = image;
        _tiles.Children.Add(image);
    }

    public void RemoveTile((int Column, int Row) key)
    {
        if (Tiles.Remove(key, out var image))
        {
            _tiles.Children.Remove(image);
        }
    }

    public void ClearTiles()
    {
        Tiles.Clear();
        _tiles.Children.Clear();
        TileScale = 0;
    }

    /// <summary>Cancels any render in flight and returns a fresh token for the next one.</summary>
    public CancellationToken RestartRendering()
    {
        RenderCancellation.Cancel();
        RenderCancellation.Dispose();
        RenderCancellation = new CancellationTokenSource();
        PendingWidth = 0;
        return RenderCancellation.Token;
    }

    /// <summary>Draws highlight rectangles (search hits, selection) given in page fractions.</summary>
    public void SetHighlights(IEnumerable<(NormalizedRect Area, Brush Brush)> highlights)
    {
        _overlay.Children.Clear();
        foreach (var (area, brush) in highlights)
        {
            var rect = new Rectangle
            {
                Fill = brush,
                Width = Math.Max(1, area.Width * Width),
                Height = Math.Max(1, area.Height * Height),
                RadiusX = 1.5,
                RadiusY = 1.5,
            };
            Canvas.SetLeft(rect, area.X * Width);
            Canvas.SetTop(rect, area.Y * Height);
            _overlay.Children.Add(rect);
        }
    }

    /// <summary>Resets the visual for reuse with another page.</summary>
    public void Reset()
    {
        RestartRendering();
        PageIndex = -1;
        SetBase(null, 0);
        ClearTiles();
        _overlay.Children.Clear();
        FormLayer.Children.Clear();
        FormLayer.Tag = null;
    }

    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
