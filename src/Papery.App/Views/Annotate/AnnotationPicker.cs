using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;

namespace Papery.Views.Annotate;

/// <summary>The Select tool: click an annotation to select it (a coral frame), drag to move it, drag the corner to resize it.</summary>
internal sealed class AnnotationPicker(
    Func<int, IReadOnlyList<PdfAnnotationInfo>> annotationsOn,
    Action<PdfAnnotationInfo, NormalizedRect> moved) : IPageTool
{
    private readonly Rectangle _frame = new()
    {
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
        StrokeThickness = 2,
        StrokeDashArray = [3, 2],
        IsHitTestVisible = false,
    };

    private readonly Rectangle _handle = new()
    {
        Width = HandleSize,
        Height = HandleSize,
        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
        IsHitTestVisible = false,
    };

    private const double HandleSize = 10;

    private PagePoint _start;
    private bool _dragging;
    private bool _resizing;
    private NormalizedRect _bounds;

    public PdfAnnotationInfo? Selected { get; private set; }

    /// <summary>Something was selected or let go of.</summary>
    public event Action? SelectionChanged;

    public void Clear()
    {
        bool had = Selected != null;
        Selected = null;
        (_frame.Parent as Panel)?.Children.Remove(_frame);
        (_handle.Parent as Panel)?.Children.Remove(_handle);
        if (had)
        {
            SelectionChanged?.Invoke();
        }
    }

    private static bool CanResize(PdfAnnotationInfo info) =>
        info.Kind is not (PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut or PdfAnnotationKind.Note);

    private bool OnHandle(PageToolInput input) =>
        Selected is { } selected && selected.PageIndex == input.Page && CanResize(selected) &&
        Math.Abs((input.Point.X - selected.Bounds.Right) * input.Width) <= HandleSize &&
        Math.Abs((input.Point.Y - selected.Bounds.Bottom) * input.Height) <= HandleSize;

    public void Press(PageToolInput input)
    {
        if (OnHandle(input))
        {
            _resizing = true;
            _dragging = false;
            _start = input.Point;
            return;
        }

        Clear();

        // The last one drawn is on top, so it's the one meant. Small targets get a little slack.
        const double Slack = 0.006;
        var hit = annotationsOn(input.Page).LastOrDefault(a =>
            input.Point.X >= a.Bounds.X - Slack && input.Point.X <= a.Bounds.Right + Slack &&
            input.Point.Y >= a.Bounds.Y - Slack && input.Point.Y <= a.Bounds.Bottom + Slack);
        if (hit == null)
        {
            return;
        }

        Selected = hit;
        _start = input.Point;
        _bounds = hit.Bounds;
        _dragging = true;
        input.Layer.Children.Add(_frame);
        if (CanResize(hit))
        {
            input.Layer.Children.Add(_handle);
        }

        Place(input);
        SelectionChanged?.Invoke();
    }

    public void Move(PageToolInput input)
    {
        if (_resizing && Selected is { } resizing)
        {
            _bounds = Resized(resizing, input);
            Place(input);
            return;
        }

        if (!_dragging || Selected == null)
        {
            return;
        }

        _bounds = Shifted(Selected.Bounds, input.Point);
        Place(input);
    }

    public void Release(PageToolInput input)
    {
        if (_resizing && Selected is { } resized)
        {
            _resizing = false;
            var target = Resized(resized, input);
            if (target != resized.Bounds)
            {
                Clear();
                moved(resized, target);
            }

            return;
        }

        if (!_dragging || Selected is not { } selected)
        {
            return;
        }

        _dragging = false;
        bool textMark = selected.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut;
        double dx = Math.Abs(input.Point.X - _start.X) * input.Width, dy = Math.Abs(input.Point.Y - _start.Y) * input.Height;
        if (!textMark && (dx > 3 || dy > 3))
        {
            var target = Shifted(selected.Bounds, input.Point);
            Clear();
            moved(selected, target);
        }
        else
        {
            _bounds = selected.Bounds; // text marks belong to their words, so they don't move
            Place(input);
        }
    }

    private NormalizedRect Shifted(NormalizedRect original, PagePoint to)
    {
        double x = Math.Clamp(original.X + to.X - _start.X, 0, Math.Max(0, 1 - original.Width));
        double y = Math.Clamp(original.Y + to.Y - _start.Y, 0, Math.Max(0, 1 - original.Height));
        return original with { X = x, Y = y };
    }

    /// <summary>Pictures keep their shape, so nobody's signature ends up stretched.</summary>
    private static NormalizedRect Resized(PdfAnnotationInfo info, PageToolInput input)
    {
        var box = info.Bounds;
        double minW = 8 / input.Width, minH = 8 / input.Height;
        double width = Math.Clamp(input.Point.X - box.X, minW, 1 - box.X);
        double height = Math.Clamp(input.Point.Y - box.Y, minH, 1 - box.Y);
        if (info.Kind is PdfAnnotationKind.Signature or PdfAnnotationKind.Image && box.Width > 0)
        {
            double scale = Math.Min(width / box.Width, (1 - box.Y) / box.Height);
            scale = Math.Max(scale, minW / box.Width);
            width = box.Width * scale;
            height = box.Height * scale;
        }

        return box with { Width = width, Height = height };
    }

    private void Place(PageToolInput input)
    {
        Canvas.SetLeft(_frame, (_bounds.X * input.Width) - 3);
        Canvas.SetTop(_frame, (_bounds.Y * input.Height) - 3);
        _frame.Width = (_bounds.Width * input.Width) + 6;
        _frame.Height = (_bounds.Height * input.Height) + 6;
        Canvas.SetLeft(_handle, (_bounds.Right * input.Width) - (HandleSize / 2));
        Canvas.SetTop(_handle, (_bounds.Bottom * input.Height) - (HandleSize / 2));
    }
}
