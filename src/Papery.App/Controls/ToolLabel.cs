using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Papery.Controls;

/// <summary>
/// A short word under a toolbar icon, as in Word's ribbon, so people can see what a button does without hovering.
/// Set <c>controls:ToolLabel.Text="Crop"</c> on a button whose content is its icon; the icon and the word are put
/// together in a small stack. <c>controls:ToolLabel.Beside="True"</c> puts the word to the right instead, for the
/// few buttons beside the mode pills, where a taller button would look out of place.
/// The tooltip still says more, and the accessible name stays the full one.
/// </summary>
public static class ToolLabel
{
    private const string Marker = "ToolLabel";

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(ToolLabel), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty BesideProperty =
        DependencyProperty.RegisterAttached("Beside", typeof(bool), typeof(ToolLabel), new PropertyMetadata(false, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static bool GetBeside(DependencyObject element) => (bool)element.GetValue(BesideProperty);

    public static void SetBeside(DependencyObject element, bool value) => element.SetValue(BesideProperty, value);

    /// <summary>Shows or hides the words of the labelled buttons in a panel, keeping their icons (for narrow windows).</summary>
    public static void ShowWords(Panel panel, bool show)
    {
        foreach (var control in panel.Children.OfType<ContentControl>())
        {
            if (control.Content is StackPanel { Tag: Marker } stack)
            {
                stack.Children[1].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Lets a toolbar that doesn't fit scroll sideways with the mouse wheel, as well as by its scrollbar, so labelled
    /// buttons past the edge are never out of reach.
    /// </summary>
    /// <param name="roomForBar">Leaves a strip under the buttons for the scrollbar, so it never covers their words.</param>
    public static void ScrollsSideways(ScrollViewer bar, bool roomForBar = true)
    {
        if (roomForBar && bar.Content is FrameworkElement content)
        {
            var margin = content.Margin;
            content.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + 8);
        }

        bar.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
        {
            if (bar.ScrollableWidth > 0)
            {
                bar.ChangeView(bar.HorizontalOffset - args.GetCurrentPoint(bar).Properties.MouseWheelDelta, null, null);
                args.Handled = true;
            }
        }), handledEventsToo: true);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl control)
        {
            return;
        }

        // XAML may set this before the icon arrives as content; finish the job once it's there.
        if (!Apply(control))
        {
            control.Loaded -= OnLoaded;
            control.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var control = (ContentControl)sender;
        control.Loaded -= OnLoaded;
        Apply(control);
    }

    private static bool Apply(ContentControl control)
    {
        string? text = GetText(control);
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (control.Content is StackPanel { Tag: Marker } existing)
        {
            ((TextBlock)existing.Children[1]).Text = text;
            return true;
        }

        if (control.Content is not UIElement icon)
        {
            return false;
        }

        bool beside = GetBeside(control);
        control.Content = null; // an element can only have one parent
        var label = new TextBlock
        {
            Text = text,
            FontSize = beside ? 13 : 11,
            TextTrimming = TextTrimming.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel
        {
            Tag = Marker,
            Orientation = beside ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = beside ? 8 : 3,
        };
        if (icon is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
        }

        stack.Children.Add(icon);
        stack.Children.Add(label);
        control.Content = stack;
        if (!beside)
        {
            // Room for a word, with the same 36px touch target as before.
            control.Padding = new Thickness(6, 5, 6, 4);
            control.MinWidth = 48;
        }

        return true;
    }
}
