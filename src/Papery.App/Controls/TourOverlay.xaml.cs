using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Services;
using Windows.Foundation;
using Windows.System;

namespace Papery.Controls;

/// <summary>Draws one tour stop: the window dimmed, the target lit and ringed, an arrow to it, and a card of words.</summary>
internal sealed partial class TourOverlay : UserControl
{
    private const double RingGap = 8;
    private const double ArrowRoom = 72;
    private const double EdgeMargin = 16;

    private FrameworkElement? _target;
    private int _seed;

    public TourOverlay()
    {
        InitializeComponent();
        NextButton.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        EndButton.Click += (_, _) => EndRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += OnKeyDown;
    }

    public event EventHandler? NextRequested;

    public event EventHandler? EndRequested;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows a stop. Each one has its own seed, so the sketch differs from step to step but doesn't shift on a resize.</summary>
    public void Show(FrameworkElement target, string title, string text, string count, bool last, int seed)
    {
        _target = target;
        _seed = seed;
        TitleText.Text = title;
        BodyText.Text = text;
        CountText.Text = count;
        NextButton.Content = last ? "Finish" : "Next";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Card, $"Tour, {count}: {title}");
        Squiggle.Data = Doodle.Squiggle(Math.Min(120, 40 + (title.Length * 5)), seed);
        Arrange();
        NextButton.Focus(FocusState.Programmatic);
        Motion.FadeIn(Stage);
    }

    /// <summary>Works everything out again for the current window size.</summary>
    public void Arrange()
    {
        if (_target == null || XamlRoot == null || XamlRoot.Content == null)
        {
            return;
        }

        var size = XamlRoot.Size;
        Width = Stage.Width = size.Width;
        Height = Stage.Height = size.Height;

        var at = _target.TransformToVisual(XamlRoot.Content).TransformBounds(new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));

        // Part of a tall control can be below the window, and the ring belongs around what can be seen.
        double left = Math.Max(EdgeMargin, at.Left - RingGap), top = Math.Max(EdgeMargin, at.Top - RingGap);
        double right = Math.Min(size.Width - EdgeMargin, at.Right + RingGap), bottom = Math.Min(size.Height - EdgeMargin, at.Bottom + RingGap);
        var ring = new Rect(left, top, Math.Max(8, right - left), Math.Max(8, bottom - top));

        Scrim.Data = Doodle.ScrimWithHole(size, ring);
        Ring.Data = Doodle.Ring(ring, _seed);

        Card.Measure(new Size(Card.Width, double.PositiveInfinity));
        var card = Place(size, ring, Card.Width, Card.DesiredSize.Height);
        Canvas.SetLeft(Card, card.X);
        Canvas.SetTop(Card, card.Y);

        var cardRect = new Rect(card.X, card.Y, Card.Width, Card.DesiredSize.Height);
        Arrow.Data = Overlaps(cardRect, ring) ? null : Doodle.Arrow(cardRect, ring, _seed);
    }

    /// <summary>Beside the target with room for the arrow, wherever fits best. A target filling the window gets the card sitting on it.</summary>
    private static Point Place(Size window, Rect ring, double width, double height)
    {
        double Across(double wanted) => Math.Clamp(wanted, EdgeMargin, Math.Max(EdgeMargin, window.Width - width - EdgeMargin));
        double Down(double wanted) => Math.Clamp(wanted, EdgeMargin, Math.Max(EdgeMargin, window.Height - height - EdgeMargin));
        double middleX = ring.X + (ring.Width / 2) - (width / 2), middleY = ring.Y + (ring.Height / 2) - (height / 2);
        double needDown = height + ArrowRoom + EdgeMargin, needAcross = width + ArrowRoom + EdgeMargin;

        if (window.Height - ring.Bottom >= needDown)
        {
            return new Point(Across(middleX), ring.Bottom + ArrowRoom);
        }

        if (ring.Top >= needDown)
        {
            return new Point(Across(middleX), ring.Top - ArrowRoom - height);
        }

        if (window.Width - ring.Right >= needAcross)
        {
            return new Point(ring.Right + ArrowRoom, Down(middleY));
        }

        if (ring.Left >= needAcross)
        {
            return new Point(ring.Left - ArrowRoom - width, Down(middleY));
        }

        return new Point(Across(middleX), Down(window.Height - height - (EdgeMargin * 2)));
    }

    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
