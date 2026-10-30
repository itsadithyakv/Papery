using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Papery.Pdf;
using Papery.Pdf.Editing;

namespace Papery.Controls;

/// <summary>One pointer event on a page, for a tool drawing on it.</summary>
/// <param name="Layer">A canvas over the page for live previews, in page-sized pixels.</param>
public sealed record PageToolInput(int Page, PagePoint Point, Canvas Layer, double Width, double Height);

/// <summary>Something that takes over the mouse or pen on the pages (the Annotate tools).</summary>
public interface IPageTool
{
    void Press(PageToolInput input);

    void Move(PageToolInput input);

    void Release(PageToolInput input);
}

/// <summary>A page came on screen, with the layer that form controls go on.</summary>
public sealed record PageShownArgs(int Page, Canvas Layer, double Width, double Height);

public sealed partial class PdfViewer
{
    private bool _formFieldsActive = true;
    private IPageTool? _tool;
    private PageVisual? _toolVisual;

    /// <summary>While set, presses and drags on pages go to this tool (touch still scrolls).</summary>
    public IPageTool? Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            ClearSelection();
        }
    }

    /// <summary>While on, dragging across text selects it as usual and then raises <see cref="TextMarked"/>.</summary>
    public bool MarkTextOnSelect { get; set; }

    /// <summary>Text was dragged across while <see cref="MarkTextOnSelect"/> is on.</summary>
    public event EventHandler<PdfTextSelection>? TextMarked;

    /// <summary>A page came on screen.</summary>
    public event EventHandler<PageShownArgs>? PageShown;

    /// <summary>Off while a drawing tool needs the whole page.</summary>
    public bool FormFieldsActive
    {
        get => _formFieldsActive;
        set
        {
            _formFieldsActive = value;
            foreach (var visual in _visuals.Values)
            {
                visual.FormLayer.Visibility = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
    }

    /// <summary>Redraws one page after its annotations changed.</summary>
    public void RefreshPage(int page)
    {
        _baseCache.RemoveWhere(k => k.Page == page);
        _tileCache.RemoveWhere(k => k.Page == page);
        if (_visuals.TryGetValue(page, out var visual))
        {
            visual.RestartRendering();
            visual.InvalidateBase();
            visual.ClearTiles();
        }

        UpdateVisibleRange(settled: true);
    }

    /// <summary>The preview canvas of a page, if it's on screen.</summary>
    public Canvas? ToolLayerFor(int page) => _visuals.TryGetValue(page, out var visual) ? visual.ToolLayer : null;

    private PageToolInput ToolInput(PageVisual visual, Microsoft.UI.Input.PointerPoint point)
    {
        var (x, y) = Normalize(visual, point.Position);

        // Only a pen reports real pressure; a mouse would read as a constant half.
        float pressure = point.PointerDeviceType == PointerDeviceType.Pen ? point.Properties.Pressure : 1;
        return new PageToolInput(visual.PageIndex, new PagePoint(x, y, pressure), visual.ToolLayer, visual.Width, visual.Height);
    }

    /// <summary>Returns true when a tool handled the press.</summary>
    private bool ToolPressed(PageVisual visual, PointerRoutedEventArgs e)
    {
        if (_tool == null)
        {
            return false;
        }

        var point = e.GetCurrentPoint(visual);
        _toolVisual = visual;
        visual.CapturePointer(e.Pointer);
        e.Handled = true;
        _tool.Press(ToolInput(visual, point));
        return true;
    }

    private bool ToolMoved(PageVisual visual, PointerRoutedEventArgs e)
    {
        if (_tool == null)
        {
            return false;
        }

        if (_toolVisual == visual)
        {
            _tool.Move(ToolInput(visual, e.GetCurrentPoint(visual)));
            e.Handled = true;
        }

        return true;
    }

    private bool ToolReleased(PageVisual visual, PointerRoutedEventArgs e)
    {
        if (_tool == null || _toolVisual != visual)
        {
            return false;
        }

        _toolVisual = null;
        visual.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        _tool.Release(ToolInput(visual, e.GetCurrentPoint(visual)));
        return true;
    }
}
