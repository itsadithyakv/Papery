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
            _paper.BorderBrush = ThemeResources.Brush(this, "PaperyCardBorderBrush");
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
        _image.Source = page.Image;
        _paper.Background = page.Paper;
        _number.Text = page.Label;
        _fromChip.Visibility = page.FromFile != null ? Visibility.Visible : Visibility.Collapsed;
        _fromText.Text = page.FromFile ?? string.Empty;
        ToolTipService.SetToolTip(_fromChip, page.FromFile);
        Resize(page.TileSize);
        AutomationProperties.SetName(this, page.AccessibleName);
        page.EnsureImage(); // a page moved by dragging may arrive without its picture
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        var page = _page!;
        switch (e.PropertyName)
        {
            case nameof(OrganisePage.Image):
                _image.Source = page.Image;
                break;
            case nameof(OrganisePage.QuarterTurns):
                TurnTo(page.QuarterTurns);
                break;
            case nameof(OrganisePage.Label):
                _number.Text = page.Label;
                break;
            case nameof(OrganisePage.TileSize):
                Resize(page.TileSize);
                break;
            case nameof(OrganisePage.Paper):
                _paper.Background = page.Paper;
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
