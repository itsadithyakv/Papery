using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Windows.Foundation;

namespace Papery.Views.Annotate;

/// <summary>
/// The drawing tools: turns presses and drags on a page into annotation drafts, with a live preview
/// on the page's tool layer. Pure gesture logic; the document view does the saving and undo.
/// </summary>
internal sealed class AnnotationPen : IPageTool
{
    private const double PointsPerCentimetre = 72 / 2.54;
    private const double CloseEnough = 0.015; // clicking this near a polygon's start closes it

    private readonly Func<int, PdfPageSize> _pageSize;
    private readonly List<PagePoint> _points = [];
    private Shape? _preview;
    private Canvas? _layer;
    private int _page = -1;

    public AnnotationPen(PdfAnnotationKind kind, PdfColor color, float width, Func<int, PdfPageSize> pageSize)
    {
        Kind = kind;
        Color = color;
        Width = width;
        _pageSize = pageSize;
    }

    /// <summary>A finished drawing. Text tools fill in their words before it's added.</summary>
    public event Action<int, PdfAnnotationDraft>? Drawn;

    public PdfAnnotationKind Kind { get; }

    public PdfColor Color { get; set; }

    public float Width { get; set; }

    /// <summary>The words on a stamp.</summary>
    public string? StampText { get; set; }

    public void Press(PageToolInput input)
    {
        if (Kind == PdfAnnotationKind.Polygon && _page == input.Page && _points.Count > 0)
        {
            AddPolygonPoint(input);
            return;
        }

        Cancel();
        _page = input.Page;
        _layer = input.Layer;
        _points.Add(input.Point);
        _preview = CreatePreview();
        input.Layer.Children.Add(_preview);
        UpdatePreview(input);
    }

    public void Move(PageToolInput input)
    {
        if (_page != input.Page || _preview == null)
        {
            return;
        }

        if (Kind is PdfAnnotationKind.Pen or PdfAnnotationKind.Marker)
        {
            // Skip points closer than half a pixel: they add file size, not detail.
            var last = _points[^1];
            if (Math.Abs(input.Point.X - last.X) * input.Width > 0.5 || Math.Abs(input.Point.Y - last.Y) * input.Height > 0.5)
            {
                _points.Add(input.Point);
            }
        }
        else if (Kind != PdfAnnotationKind.Polygon)
        {
            if (_points.Count > 1)
            {
                _points[1] = input.Point;
            }
            else
            {
                _points.Add(input.Point);
            }
        }

        UpdatePreview(input);
    }

    public void Release(PageToolInput input)
    {
        if (_page != input.Page || Kind == PdfAnnotationKind.Polygon)
        {
            return; // a polygon goes on until it's closed
        }

        var start = _points[0];
        var end = _points.Count > 1 ? _points[^1] : start;
        var box = Box(start, end);
        bool isClick = box.Width * input.Width < 4 && box.Height * input.Height < 4;
        int page = _page;
        var points = _points.ToArray();
        Cancel();

        var size = _pageSize(page);
        var draft = Kind switch
        {
            PdfAnnotationKind.Pen or PdfAnnotationKind.Marker => Stroke(points),
            PdfAnnotationKind.Note => new PdfAnnotationDraft(Kind, Color, [new NormalizedRect(start.X, start.Y, 0.01, 0.01)]),
            PdfAnnotationKind.Stamp => new PdfAnnotationDraft(Kind, Color, [StampBox(start, end, isClick, size)], Width: 2, Text: StampText ?? "APPROVED"),
            PdfAnnotationKind.TextBox => isClick
                ? new PdfAnnotationDraft(Kind, Color, [Clamp(new NormalizedRect(start.X, start.Y, 0.35, 0.08))], FontSize: 12)
                : new PdfAnnotationDraft(Kind, Color, [box], FontSize: 12),
            PdfAnnotationKind.Callout when !isClick => Callout(start, end),
            PdfAnnotationKind.Measure when !isClick => Measure(start, end, size),
            PdfAnnotationKind.Line or PdfAnnotationKind.Arrow when !isClick => Stroke([start, end]),
            PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse or PdfAnnotationKind.Cloud when !isClick => new PdfAnnotationDraft(Kind, Color, [box], Width: Width),
            _ => null, // a click with a drag tool draws nothing
        };
        if (draft != null)
        {
            Drawn?.Invoke(page, draft);
        }
    }

    /// <summary>Forgets an unfinished drawing (switching tools, or Esc).</summary>
    public void Cancel()
    {
        if (_preview != null)
        {
            _layer?.Children.Remove(_preview);
        }

        _preview = null;
        _layer = null;
        _page = -1;
        _points.Clear();
    }

    private void AddPolygonPoint(PageToolInput input)
    {
        var first = _points[0];
        if (_points.Count >= 3 && Math.Abs(input.Point.X - first.X) < CloseEnough && Math.Abs(input.Point.Y - first.Y) < CloseEnough)
        {
            int page = _page;
            var points = _points.ToArray();
            Cancel();
            Drawn?.Invoke(page, new PdfAnnotationDraft(Kind, Color, Strokes: [points], Width: Width));
            return;
        }

        _points.Add(input.Point);
        UpdatePreview(input);
    }

    private PdfAnnotationDraft Stroke(IReadOnlyList<PagePoint> points) => new(Kind, Color, Strokes: [points], Width: Width);

    private PdfAnnotationDraft Callout(PagePoint target, PagePoint boxAt)
    {
        // Drag from what it points at to where the words go.
        var box = Clamp(new NormalizedRect(boxAt.X, boxAt.Y, 0.3, 0.08));
        var edge = new PagePoint(boxAt.X >= target.X ? box.X : box.Right, box.Y + (box.Height / 2));
        return new PdfAnnotationDraft(Kind, Color, [box], [new[] { edge, target }], Width, FontSize: 12);
    }

    private PdfAnnotationDraft Measure(PagePoint a, PagePoint b, PdfPageSize size)
    {
        double dx = (b.X - a.X) * size.Width, dy = (b.Y - a.Y) * size.Height;
        double centimetres = Math.Sqrt((dx * dx) + (dy * dy)) / PointsPerCentimetre;
        return new PdfAnnotationDraft(Kind, Color, Strokes: [new[] { a, b }], Width: Width, Text: $"{centimetres:0.0} cm", FontSize: 11);
    }

    /// <summary>A click drops a stamp in a sensible shape (about 6 × 1.5 cm); a drag sets its size.</summary>
    private static NormalizedRect StampBox(PagePoint start, PagePoint end, bool isClick, PdfPageSize size)
    {
        if (!isClick)
        {
            return Box(start, end);
        }

        double w = 170 / size.Width, h = 44 / size.Height;
        return Clamp(new NormalizedRect(start.X - (w / 2), start.Y - (h / 2), w, h));
    }

    private static NormalizedRect Box(PagePoint a, PagePoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static NormalizedRect Clamp(NormalizedRect r)
    {
        double w = Math.Min(r.Width, 1), h = Math.Min(r.Height, 1);
        return new NormalizedRect(Math.Clamp(r.X, 0, 1 - w), Math.Clamp(r.Y, 0, 1 - h), w, h);
    }

    private Shape CreatePreview()
    {
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
        Shape shape = Kind switch
        {
            PdfAnnotationKind.Ellipse => new Ellipse(),
            PdfAnnotationKind.Rectangle or PdfAnnotationKind.Cloud or PdfAnnotationKind.TextBox or PdfAnnotationKind.Stamp => new Rectangle(),
            _ => new Polyline { StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
        };
        shape.Stroke = brush;
        shape.StrokeThickness = Math.Max(1, Width);
        return shape;
    }

    private void UpdatePreview(PageToolInput input)
    {
        double zoomX = input.Width, zoomY = input.Height;
        if (_preview is Polyline line)
        {
            var points = new PointCollection();
            foreach (var p in _points)
            {
                points.Add(new Point(p.X * zoomX, p.Y * zoomY));
            }

            if (Kind == PdfAnnotationKind.Polygon)
            {
                points.Add(new Point(input.Point.X * zoomX, input.Point.Y * zoomY));
            }

            line.Points = points;
        }
        else if (_preview != null && _points.Count > 0)
        {
            var box = Box(_points[0], _points[^1]);
            Canvas.SetLeft(_preview, box.X * zoomX);
            Canvas.SetTop(_preview, box.Y * zoomY);
            _preview.Width = box.Width * zoomX;
            _preview.Height = box.Height * zoomY;
        }
    }
}
