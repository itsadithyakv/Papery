using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Papery.Services;

namespace Papery.Controls;

/// <summary>
/// A row of pill choices with one soft coral "blob" behind the selected item that slides and
/// stretches smoothly to the next choice. Used for the mode bar and the sidebar tabs.
/// </summary>
/// <remarks>
/// Each choice is a real RadioButton underneath, so Tab enters the group, the arrow keys move
/// between choices, and Narrator reads "View, radio button, 1 of 8, selected".
/// </remarks>
public sealed partial class PillBar : Grid
{
    private readonly Border _blob;
    private readonly TranslateTransform _blobOffset = new();
    private readonly List<RadioButton> _items = [];
    private readonly string _group = $"pillbar-{Guid.NewGuid():N}";
    private Panel _row = null!;
    private int _selected = -1;
    private bool _equalWidths;

    public PillBar()
    {
        _blob = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(17),
            RenderTransform = _blobOffset,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        _blob.SetValue(Border.BackgroundProperty, null);
        Children.Add(_blob);
        BuildRow();
        SizeChanged += (_, _) => MoveBlob(animate: false);
        Loaded += (_, _) =>
        {
            // Theme-aware colour (a XAML style would also do; this keeps the control self-contained).
            _blob.Background = ThemeResources.Brush(this, "PaperyCoralSubtleBrush");
            MoveBlob(animate: false);
        };
        ActualThemeChanged += (_, _) => _blob.Background = ThemeResources.Brush(this, "PaperyCoralSubtleBrush");
    }

    /// <summary>Raised with the new index when the user picks a choice.</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>True: choices share the full width equally (sidebar). False: each is as wide as its text.</summary>
    public bool EqualWidths
    {
        get => _equalWidths;
        set
        {
            _equalWidths = value;
            BuildRow();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value >= 0 && value < _items.Count && value != _selected)
            {
                _items[value].IsChecked = true; // raises Checked -> Select()
            }
        }
    }

    public IReadOnlyList<RadioButton> Items => _items;

    /// <summary>An icon-only choice, with the name as its tooltip and for Narrator.</summary>
    public void AddIconItem(string glyph, string name)
    {
        AddItem(name);
        var item = _items[^1];
        item.Content = new FontIcon { Glyph = glyph, FontSize = 16, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] };
        ToolTipService.SetToolTip(item, name);
    }

    public void AddItem(string text, string? glyph = null, string? tag = null, string? accessibleName = null)
    {
        object content = text;
        if (glyph != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] });
            row.Children.Add(new TextBlock { Text = text });
            content = row;
        }

        var item = new RadioButton
        {
            Content = content,
            Tag = tag ?? text,
            GroupName = _group,
            Style = (Style)Application.Current.Resources["PaperyPillItemStyle"],
            HorizontalAlignment = _equalWidths ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };
        if (_equalWidths)
        {
            item.Padding = new Thickness(6, 6, 6, 6); // equal thirds are narrow: give the words the room
        }

        AutomationProperties.SetName(item, accessibleName ?? text);
        int index = _items.Count;
        item.Checked += (_, _) => Select(index);
        _items.Add(item);

        if (_row is Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(item, index);
        }

        _row.Children.Add(item);
        if (_selected < 0)
        {
            item.IsChecked = true;
        }
    }

    private void BuildRow()
    {
        if (_row != null)
        {
            Children.Remove(_row);
        }

        _row = _equalWidths
            ? new Grid { ColumnSpacing = 2 }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _row.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        _row.TabFocusNavigation = KeyboardNavigationMode.Once;
        Children.Add(_row);

        var existing = _items.ToList();
        _items.Clear();
        _selected = -1;
        foreach (var item in existing)
        {
            item.HorizontalAlignment = _equalWidths ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            (item.Parent as Panel)?.Children.Remove(item);
            int index = _items.Count;
            _items.Add(item);
            if (_row is Grid grid)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(item, index);
            }

            _row.Children.Add(item);
        }
    }

    private void Select(int index)
    {
        if (index == _selected)
        {
            return;
        }

        bool first = _selected < 0;
        _selected = index;
        MoveBlob(animate: !first);
        SelectionChanged?.Invoke(this, index);
    }

    /// <summary>Slides and stretches the blob to sit exactly behind the selected choice.</summary>
    private void MoveBlob(bool animate)
    {
        if (_selected < 0 || _selected >= _items.Count || _items[_selected].ActualWidth <= 0)
        {
            return;
        }

        var item = _items[_selected];
        var origin = item.TransformToVisual(this).TransformPoint(default);
        double x = origin.X, width = item.ActualWidth;
        _blob.Height = item.ActualHeight;
        _blob.Margin = new Thickness(0, origin.Y, 0, 0);
        _blob.Opacity = 1;

        if (!animate || !Motion.Enabled || double.IsNaN(_blob.Width))
        {
            _blobOffset.X = x;
            _blob.Width = width;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        var slide = new DoubleAnimation { To = x, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, _blobOffset);
        Storyboard.SetTargetProperty(slide, "X");

        // Width isn't a GPU property, so it's a "dependent" animation; it's short, so that's fine.
        var stretch = new DoubleAnimation { To = width, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(stretch, _blob);
        Storyboard.SetTargetProperty(stretch, "Width");

        var story = new Storyboard();
        story.Children.Add(slide);
        story.Children.Add(stretch);
        story.Begin();
    }
}
