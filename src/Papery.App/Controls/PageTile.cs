using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Papery.Services;
using Papery.Views.Organise;

namespace Papery.Controls;

/// <summary>
/// A page on the Organise board: the page itself (turning smoothly when rotated), its number,
/// a chip when it came from another file, and two quick buttons that appear on hover.
/// </summary>
/// <remarks>
/// Built in code and driven by its DataContext (an <see cref="OrganisePage"/>), because the
/// grid recycles tiles while scrolling: a tile must switch pages instantly, but animate when
/// the page it already shows is turned.
/// </remarks>
public sealed partial class PageTile : Grid
{
    private const double Inset = 10;

    private readonly Grid _box = new();
    private readonly Border _paper = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly RotateTransform _rotation = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBlock _number = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fromChip = new();
    private readonly TextBlock _fromText = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    // A reshaped page (cropped, new paper, margins, straightened) is drawn as its result instead:
    // the paper, and through a clipped window on it, the page turned and placed as it will be saved.
    private readonly Grid _shaped = new() { Visibility = Visibility.Collapsed };
    private readonly Border _shapedPaper = new();
    private readonly Canvas _window = new();
    private readonly Image _shapedImage = new() { Stretch = Stretch.Fill };
    private OrganisePage? _page;
    private double _angle;

    public PageTile()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowSpacing = 4;

        _paper.Child = _image;
        _paper.BorderThickness = new Thickness(1);
        _paper.CornerRadius = new CornerRadius(3);
        _paper.HorizontalAlignment = HorizontalAlignment.Center;
        _paper.VerticalAlignment = VerticalAlignment.Center;
        _paper.RenderTransform = _rotation;
        _paper.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _box.Children.Add(_paper);

        _shapedPaper.BorderThickness = new Thickness(1);
        _shapedPaper.CornerRadius = new CornerRadius(3);
        _shapedPaper.HorizontalAlignment = HorizontalAlignment.Center;
        _shapedPaper.VerticalAlignment = VerticalAlignment.Center;
        _window.HorizontalAlignment = HorizontalAlignment.Left;
        _window.VerticalAlignment = VerticalAlignment.Top;
        _window.Children.Add(_shapedImage);
        var paperArea = new Grid();
        paperArea.Children.Add(_window);
        _shapedPaper.Child = paperArea;
        _shaped.Children.Add(_shapedPaper);
        _box.Children.Add(_shaped);

        _actions.HorizontalAlignment = HorizontalAlignment.Right;
        _actions.VerticalAlignment = VerticalAlignment.Top;
        _actions.Margin = new Thickness(0, 2, 2, 0);
        _actions.Opacity = 0;
        _actions.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        _actions.Children.Add(QuickButton("", "Rotate right", () => _page?.Actions.RotatePage(_page, 1)));
        _actions.Children.Add(QuickButton("", "Delete page", () => _page?.Actions.DeletePage(_page)));
        _box.Children.Add(_actions);
        Children.Add(_box);

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        label.Children.Add(_number);
        _fromChip.Child = _fromText;
        label.Children.Add(_fromChip);
        SetRow(label, 1);
        Children.Add(label);

        Loaded += (_, _) =>
        {
            // Theme brushes are looked up once on screen, so they follow Papery's theme, not Windows'.
            _paper.BorderBrush = _shapedPaper.BorderBrush = ThemeResources.Brush(this, "PaperyCardBorderBrush");
            _number.Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"];
            _fromChip.Style = (Style)Application.Current.Resources["PaperyChipStyle"];
        };
        PointerEntered += (_, _) => _actions.Opacity = 1;
        PointerExited += (_, _) => _actions.Opacity = 0;
        DataContextChanged += (_, _) => Attach(DataContext as OrganisePage);
    }

    private void Attach(OrganisePage? page)
    {
        if (ReferenceEquals(page, _page))
        {
            return;
        }

        if (_page != null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = page;
        _actions.Opacity = 0;
        if (page == null)
        {
            return;
        }

        page.PropertyChanged += OnPageChanged;
        // A recycled tile showing a different page: jump straight to its rotation.
        _angle = page.QuarterTurns * 90;
        _rotation.Angle = _angle;
        _image.Source = _shapedImage.Source = page.Image;
        _paper.Background = _shapedPaper.Background = page.Paper;
        _number.Text = page.Label;
        _fromChip.Visibility = page.FromFile != null ? Visibility.Visible : Visibility.Collapsed;
        _fromText.Text = page.FromFile ?? string.Empty;
        ToolTipService.SetToolTip(_fromChip, page.FromFile);
        Resize(page.TileSize);
        UpdateShape();
        AutomationProperties.SetName(this, page.AccessibleName);
        page.EnsureImage(); // a page moved by dragging may arrive without its picture
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        var page = _page!;
        switch (e.PropertyName)
        {
            case nameof(OrganisePage.Image):
                _image.Source = _shapedImage.Source = page.Image;
                break;
            case nameof(OrganisePage.QuarterTurns):
                TurnTo(page.QuarterTurns);
                UpdateShape();
                break;
            case nameof(OrganisePage.Geometry):
                UpdateShape();
                break;
            case nameof(OrganisePage.Label):
                _number.Text = page.Label;
                break;
            case nameof(OrganisePage.TileSize):
                Resize(page.TileSize);
                UpdateShape();
                break;
            case nameof(OrganisePage.Paper):
                _paper.Background = _shapedPaper.Background = page.Paper;
                break;
            case nameof(OrganisePage.AccessibleName):
                AutomationProperties.SetName(this, page.AccessibleName);
                break;
        }
    }

    /// <summary>Turns the page the short way round (a turn from 270° to 0° goes forward 90°, not back 270°).</summary>
    private void TurnTo(int quarterTurns)
    {
        int current = (((int)Math.Round(_angle / 90) % 4) + 4) % 4;
        int diff = (((quarterTurns - current) % 4) + 4) % 4;
        if (diff == 3)
        {
            diff = -1;
        }

        double target = _angle + (diff * 90);
        if (!Motion.Enabled)
        {
            _angle = _rotation.Angle = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = _rotation.Angle,
            To = target,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, _rotation);
        Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
        _angle = target;
    }

    /// <summary>Draws a reshaped page as it will be saved, using the same maths as the engine (PageGeometry.Layout).</summary>
    private void UpdateShape()
    {
        var page = _page;
        if (page?.Geometry is not { IsEmpty: false } geometry)
        {
            _shaped.Visibility = Visibility.Collapsed;
            _paper.Visibility = Visibility.Visible;
            return;
        }

        _paper.Visibility = Visibility.Collapsed;
        _shaped.Visibility = Visibility.Visible;
        var layout = geometry.Layout(page.TurnedSize);
        double available = Math.Max(20, page.TileSize - (2 * Inset));
        double scale = available / Math.Max(layout.Paper.Width, layout.Paper.Height);
        double paperW = Math.Max(8, layout.Paper.Width * scale), paperH = Math.Max(8, layout.Paper.Height * scale);
        _shapedPaper.Width = paperW;
        _shapedPaper.Height = paperH;

        // The window on the paper where the kept part of the page shows.
        double windowW = layout.Content.Width * paperW, windowH = layout.Content.Height * paperH;
        _window.Margin = new Thickness(layout.Content.X * paperW, layout.Content.Y * paperH, 0, 0);
        _window.Width = windowW;
        _window.Height = windowH;
        _window.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, windowW, windowH) };

        // The whole turned page at this scale, shifted so the kept part fills the window.
        double fullW = windowW / layout.Source.Width, fullH = windowH / layout.Source.Height;
        bool sideways = page.QuarterTurns % 2 == 1;
        double imageW = sideways ? fullH : fullW, imageH = sideways ? fullW : fullH;
        double centreX = (-layout.Source.X * fullW) + (fullW / 2), centreY = (-layout.Source.Y * fullH) + (fullH / 2);
        _shapedImage.Width = imageW;
        _shapedImage.Height = imageH;
        Canvas.SetLeft(_shapedImage, centreX - (imageW / 2));
        Canvas.SetTop(_shapedImage, centreY - (imageH / 2));
        _shapedImage.RenderTransform = new RotateTransform
        {
            Angle = (page.QuarterTurns * 90) + geometry.StraightenDegrees,
            CenterX = imageW / 2,
            CenterY = imageH / 2,
        };
    }

    /// <summary>Fits the page inside a square, so it still fits after any rotation.</summary>
    private void Resize(double tile)
    {
        var page = _page!;
        Width = tile;
        _box.Width = _box.Height = tile;
        double available = Math.Max(20, tile - (2 * Inset));
        double scale = available / Math.Max(page.Size.Width, page.Size.Height);
        _paper.Width = Math.Max(8, page.Size.Width * scale);
        _paper.Height = Math.Max(8, page.Size.Height * scale);
        _fromText.MaxWidth = Math.Max(40, tile - 48);
    }

    private static Button QuickButton(string glyph, string name, Action click)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            IsTabStop = false, // keyboard users have the toolbar and shortcuts; these are a mouse convenience
            Content = new FontIcon { Glyph = glyph, FontSize = 13, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] },
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
