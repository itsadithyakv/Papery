using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Imaging;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Organise;
using Windows.Foundation;

namespace Papery.Views;

/// <summary>Which pages a crop goes to.</summary>
public enum CropScope
{
    ThisPage,
    Selected,
    All,
    Odd,
    Even,
}

/// <summary>What the crop editor decided: the box (null = whole page) and where it applies.</summary>
public sealed record CropChoice(NormalizedRect? Crop, CropScope Scope);

/// <summary>
/// The crop box editor, shown over the Organise board. Works on the page as the user sees it
/// (after any turns), in fractions of the page, so the same box can be applied to other pages.
/// </summary>
public sealed partial class CropEditor : UserControl
{
    private const double Grab = 12;          // how close (in pixels) counts as grabbing an edge
    private const double Smallest = 0.03;    // the box never gets smaller than 3% of the page
    private const int RenderLongSide = 1800; // sharp enough on a 4K screen, quick to render

    private readonly List<Border> _handles = [];
    private NormalizedRect _box = PageGeometry.Whole;
    private OrganisePage? _page;
    private PdfBitmap? _rendered;
    private Edge _dragging;
    private Point _dragStart;
    private NormalizedRect _boxAtStart;

    public CropEditor()
    {
        InitializeComponent();
        for (int i = 0; i < 8; i++)
        {
            var handle = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false, // the canvas works out what's grabbed, with a generous margin
            };
            _handles.Add(handle);
            Overlay.Children.Add(handle);
        }

        Loaded += (_, _) =>
        {
            foreach (var handle in _handles)
            {
                handle.Background = ThemeResources.Brush(this, "PaperyCardBrush");
                handle.BorderBrush = ThemeResources.Brush(this, "PaperyCoralBrush");
            }
        };
    }

    [Flags]
    private enum Edge
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        Move = 16,
        New = 32,
    }

    /// <summary>Raised with the choice, or null when cancelled.</summary>
    public event EventHandler<CropChoice?>? Closed;

    public async Task ShowAsync(OrganisePage page, int selectedCount)
    {
        _page = page;
        _box = page.Geometry?.Crop ?? PageGeometry.Whole;
        PageSheet.Background = page.Paper;
        ApplyTo.AddItem("This page", null, nameof(CropScope.ThisPage));
        if (selectedCount > 1)
        {
            ApplyTo.AddItem($"Selected ({selectedCount})", null, nameof(CropScope.Selected));
        }

        ApplyTo.AddItem("All pages", null, nameof(CropScope.All));
        ApplyTo.AddItem("Odd", null, nameof(CropScope.Odd), "Odd pages");
        ApplyTo.AddItem("Even", null, nameof(CropScope.Even), "Even pages");
        ApplyTo.SelectedIndex = selectedCount > 1 ? 1 : 0;
        Layout();

        if (page.Document is not { } document)
        {
            Spinner.IsActive = false;
            return; // a blank page: the box works on plain paper
        }

        try
        {
            var size = page.Size;
            double scale = RenderLongSide / Math.Max(size.Width, size.Height);
            int w = Math.Max(16, (int)(size.Width * scale)), h = Math.Max(16, (int)(size.Height * scale));
            var bitmap = await document.RenderPageAsync(page.Reference.Page, w, h, PdfRenderOptions.Annotations, PdfWorkPriority.Visible);
            int turns = page.QuarterTurns;
            _rendered = await Task.Run(() => PageBitmaps.Turn(bitmap, turns));
            PageImage.Source = PageBitmaps.ToWriteableBitmap(_rendered);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
            Log.Error("Crop preview failed", ex);
        }
        finally
        {
            Spinner.IsActive = false;
            Spinner.Visibility = Visibility.Collapsed;
        }
    }

    // ================================================================= layout

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    private void Layout()
    {
        if (_page == null)
        {
            return;
        }

        var turned = _page.TurnedSize;
        double availableW = Math.Max(50, Stage.ActualWidth - 80), availableH = Math.Max(50, Stage.ActualHeight - 80);
        double scale = Math.Min(availableW / turned.Width, availableH / turned.Height);
        PageHost.Width = Overlay.Width = turned.Width * scale;
        PageHost.Height = Overlay.Height = turned.Height * scale;
        UpdateBox();
    }

    private void UpdateBox()
    {
        double w = PageHost.Width, h = PageHost.Height;
        if (double.IsNaN(w) || _page == null)
        {
            return;
        }

        double left = _box.X * w, top = _box.Y * h, right = _box.Right * w, bottom = _box.Bottom * h;
        Place(ShadeTop, 0, 0, w, top);
        Place(ShadeBottom, 0, bottom, w, h - bottom);
        Place(ShadeLeft, 0, top, left, bottom - top);
        Place(ShadeRight, right, top, w - right, bottom - top);
        Place(Frame, left, top, right - left, bottom - top);

        double midX = (left + right) / 2, midY = (top + bottom) / 2;
        Point[] spots = [new(left, top), new(midX, top), new(right, top), new(right, midY), new(right, bottom), new(midX, bottom), new(left, bottom), new(left, midY)];
        for (int i = 0; i < spots.Length; i++)
        {
            Canvas.SetLeft(_handles[i], spots[i].X - 7);
            Canvas.SetTop(_handles[i], spots[i].Y - 7);
        }

        // The size people will get, in millimetres, as on a ruler.
        var turned = _page.TurnedSize;
        double mmW = turned.Width * _box.Width / PaperSizes.PointsPerMillimetre, mmH = turned.Height * _box.Height / PaperSizes.PointsPerMillimetre;
        MeasureText.Text = $"{mmW:0} × {mmH:0} mm";

        static void Place(FrameworkElement element, double x, double y, double width, double height)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            element.Width = Math.Max(0, width);
            element.Height = Math.Max(0, height);
        }
    }

    // ================================================================= dragging

    private Edge HitTest(Point p)
    {
        double w = PageHost.Width, h = PageHost.Height;
        double left = _box.X * w, top = _box.Y * h, right = _box.Right * w, bottom = _box.Bottom * h;
        bool withinX = p.X >= left - Grab && p.X <= right + Grab, withinY = p.Y >= top - Grab && p.Y <= bottom + Grab;
        var edge = Edge.None;
        if (withinY && Math.Abs(p.X - left) <= Grab)
        {
            edge |= Edge.Left;
        }
        else if (withinY && Math.Abs(p.X - right) <= Grab)
        {
            edge |= Edge.Right;
        }

        if (withinX && Math.Abs(p.Y - top) <= Grab)
        {
            edge |= Edge.Top;
        }
        else if (withinX && Math.Abs(p.Y - bottom) <= Grab)
        {
            edge |= Edge.Bottom;
        }

        if (edge != Edge.None)
        {
            return edge;
        }

        return p.X > left && p.X < right && p.Y > top && p.Y < bottom ? Edge.Move : Edge.New;
    }

    private static InputSystemCursorShape CursorFor(Edge edge) => edge switch
    {
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => InputSystemCursorShape.SizeNortheastSouthwest,
        Edge.Left or Edge.Right => InputSystemCursorShape.SizeWestEast,
        Edge.Top or Edge.Bottom => InputSystemCursorShape.SizeNorthSouth,
        Edge.Move => InputSystemCursorShape.SizeAll,
        _ => InputSystemCursorShape.Cross,
    };

    private void OnOverlayPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(Overlay).Position;
        _dragging = HitTest(_dragStart);
        _boxAtStart = _box;
        Overlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnOverlayMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Overlay).Position;
        if (_dragging == Edge.None)
        {
            Overlay.SetCursor(CursorFor(HitTest(p)));
            return;
        }

        double w = PageHost.Width, h = PageHost.Height;
        double dx = (p.X - _dragStart.X) / w, dy = (p.Y - _dragStart.Y) / h;
        var b = _boxAtStart;
        double left = b.X, top = b.Y, right = b.Right, bottom = b.Bottom;
        if (_dragging == Edge.New)
        {
            double x1 = _dragStart.X / w, y1 = _dragStart.Y / h, x2 = p.X / w, y2 = p.Y / h;
            (left, right) = (Math.Min(x1, x2), Math.Max(x1, x2));
            (top, bottom) = (Math.Min(y1, y2), Math.Max(y1, y2));
        }
        else if (_dragging == Edge.Move)
        {
            dx = Math.Clamp(dx, -b.X, 1 - b.Right);
            dy = Math.Clamp(dy, -b.Y, 1 - b.Bottom);
            (left, right, top, bottom) = (left + dx, right + dx, top + dy, bottom + dy);
        }
        else
        {
            if (_dragging.HasFlag(Edge.Left))
            {
                left = Math.Min(left + dx, right - Smallest);
            }

            if (_dragging.HasFlag(Edge.Right))
            {
                right = Math.Max(right + dx, left + Smallest);
            }

            if (_dragging.HasFlag(Edge.Top))
            {
                top = Math.Min(top + dy, bottom - Smallest);
            }

            if (_dragging.HasFlag(Edge.Bottom))
            {
                bottom = Math.Max(bottom + dy, top + Smallest);
            }
        }

        left = Math.Clamp(left, 0, 1);
        top = Math.Clamp(top, 0, 1);
        right = Math.Clamp(right, 0, 1);
        bottom = Math.Clamp(bottom, 0, 1);
        if (right - left >= Smallest && bottom - top >= Smallest)
        {
            _box = new NormalizedRect(left, top, right - left, bottom - top);
            UpdateBox();
        }

        e.Handled = true;
    }

    private void OnOverlayReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = Edge.None;
        Overlay.ReleasePointerCaptures();
    }

    // ================================================================= buttons

    private void OnFitContent(object sender, RoutedEventArgs e)
    {
        if (_rendered is not { } page)
        {
            return;
        }

        if (PageImageAnalysis.ContentBounds(page.Pixels, page.Width, page.Height, page.Stride) is not { } bounds)
        {
            MeasureText.Text = "This page looks blank";
            return;
        }

        // A little breathing room, so letters at the edge aren't shaved.
        const double Pad = 0.015;
        double left = Math.Max(0, bounds.X - Pad), top = Math.Max(0, bounds.Y - Pad);
        double right = Math.Min(1, bounds.X + bounds.Width + Pad), bottom = Math.Min(1, bounds.Y + bounds.Height + Pad);
        _box = new NormalizedRect(left, top, right - left, bottom - top);
        UpdateBox();
    }

    private void OnWholePage(object sender, RoutedEventArgs e)
    {
        _box = PageGeometry.Whole;
        UpdateBox();
    }

    private void OnApply(object sender, RoutedEventArgs e) => Finish(apply: true);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(apply: false);

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(apply: false);
    }

    private void OnEnter(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(apply: true);
    }

    private void Finish(bool apply)
    {
        if (!apply)
        {
            Closed?.Invoke(this, null);
            return;
        }

        bool whole = _box.X < 0.002 && _box.Y < 0.002 && _box.Right > 0.998 && _box.Bottom > 0.998;
        var scope = Enum.Parse<CropScope>((string)ApplyTo.Items[Math.Max(0, ApplyTo.SelectedIndex)].Tag);
        Closed?.Invoke(this, new CropChoice(whole ? null : _box, scope));
    }
}
