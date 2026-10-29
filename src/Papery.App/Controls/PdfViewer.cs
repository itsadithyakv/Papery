using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Storage;
using Papery.Core.Viewing;
using Papery.Pdf;
using Windows.Foundation;

namespace Papery.Controls;

/// <summary>How the zoom follows the window size.</summary>
public enum FitMode
{
    /// <summary>A fixed zoom chosen by the user.</summary>
    None,

    /// <summary>The page width fills the window, and keeps doing so when it is resized.</summary>
    Width,

    /// <summary>The whole page (or spread) fits in the window.</summary>
    Page,
}

/// <summary>
/// The page viewer. Lays pages out with <see cref="DocumentLayout"/>, lets a ScrollViewer do
/// scrolling and zooming (smooth, GPU-accelerated, pinch and Ctrl+wheel for free), and only
/// creates visuals and renders bitmaps for pages near the viewport.
/// </summary>
/// <remarks>
/// Split into partial files: PdfViewer.Rendering.cs (bitmaps and tiles),
/// PdfViewer.Interaction.cs (links, text selection, keys).
/// Coordinates: the surface is laid out in DIPs at 100% zoom; the ScrollViewer scales it.
/// </remarks>
public sealed partial class PdfViewer : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly Canvas _surface;
    private readonly Dictionary<int, PageVisual> _visuals = new();
    private readonly Stack<PageVisual> _pool = new();
    private readonly DispatcherQueueTimer _settleTimer;

    private PdfDocument? _document;
    private DocumentLayout? _layout;
    private ViewMode _viewMode = ViewMode.Continuous;
    private ReadingMode _readingMode = ReadingMode.Light;
    private int _currentPage;
    private double _renderedZoom;
    private bool _presenting;

    // ScrollViewer.ChangeView is asynchronous: a zoom we've asked for isn't in ZoomFactor yet.
    // A second ChangeView (e.g. "scroll to page") must carry that zoom, or it cancels it.
    private double? _pendingZoom;

    private double TargetZoom => _pendingZoom ?? _scroll.ZoomFactor;

    public PdfViewer()
    {
        _surface = new Canvas { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        _scroll = new ScrollViewer
        {
            Content = _surface,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = (float)Zoom.Min,
            MaxZoomFactor = (float)Zoom.Max,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            FocusVisualMargin = new Thickness(3),
            FocusVisualPrimaryThickness = new Thickness(2),
            FocusVisualSecondaryThickness = new Thickness(0),
        };
        _scroll.SetValue(AutomationPropertiesHelper.NameProperty, "Document pages");
        _scroll.ViewChanged += OnViewChanged;
        _scroll.SizeChanged += (_, _) => OnViewportResized();
        Content = _scroll;
        UpdateCanvasBackground();

        _settleTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) => UpdateVisibleRange(settled: true);

        Loaded += (_, _) => XamlRoot.Changed += OnXamlRootChanged;
        Unloaded += (_, _) =>
        {
            if (XamlRoot != null)
            {
                XamlRoot.Changed -= OnXamlRootChanged;
            }
        };
        ActualThemeChanged += (_, _) => UpdateCanvasBackground();
        InitializeInteraction();
    }

    /// <summary>Raised when the page in the middle of the view changes. Argument: zero-based page.</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>Raised when the zoom level or fit mode changes.</summary>
    public event EventHandler? ZoomChanged;

    public PdfDocument? Document => _document;

    public int CurrentPage => _currentPage;

    public double ZoomFactor => _scroll.ZoomFactor;

    public FitMode FitMode { get; private set; } = FitMode.Width;

    public ViewMode ViewMode => _viewMode;

    public ReadingMode ReadingMode => _readingMode;

    /// <summary>Shows a document. <paramref name="zoom"/> of 0 means fit width, -1 fit page.</summary>
    public void SetDocument(PdfDocument? document, int page = 0, double zoom = 0, ViewMode viewMode = ViewMode.Continuous, ReadingMode readingMode = ReadingMode.Light)
    {
        ClearVisuals();
        ClearCaches();
        _document = document;
        _viewMode = viewMode;
        _readingMode = readingMode;
        _currentPage = document == null ? 0 : Math.Clamp(page, 0, document.PageCount - 1);
        FitMode = zoom switch { 0 => FitMode.Width, < 0 => FitMode.Page, _ => FitMode.None };
        RebuildLayout();

        if (document == null)
        {
            return;
        }

        // Wait for the first layout pass so the viewport size is known, then position.
        void Position()
        {
            if (FitMode == FitMode.None)
            {
                _scroll.ChangeView(null, null, (float)Zoom.Clamp(zoom), true);
            }
            else
            {
                ApplyFit();
            }

            GoToPage(_currentPage);
            UpdateVisibleRange(settled: true);
        }

        if (_scroll.ViewportWidth > 0)
        {
            Position();
        }
        else
        {
            void OnFirstLayout(object? s, object e)
            {
                _scroll.LayoutUpdated -= OnFirstLayout;
                DispatcherQueue.TryEnqueue(Position);
            }

            _scroll.LayoutUpdated += OnFirstLayout;
        }
    }

    public void SetViewMode(ViewMode mode)
    {
        if (mode == _viewMode)
        {
            return;
        }

        int page = _currentPage;
        _viewMode = mode;
        RebuildLayout();
        if (FitMode != FitMode.None)
        {
            ApplyFit();
        }

        GoToPage(page);
    }

    public void SetReadingMode(ReadingMode mode)
    {
        if (mode == _readingMode)
        {
            return;
        }

        _readingMode = mode;
        foreach (var visual in _visuals.Values)
        {
            visual.RestartRendering();
            visual.InvalidateBase();
            visual.ClearTiles();
            visual.SetPaperColor(PaperColor());
        }

        UpdateVisibleRange(settled: true);
    }

    /// <summary>Presentation mode: black surround, one page fitted to the screen.</summary>
    public void SetPresenting(bool presenting)
    {
        _presenting = presenting;
        _scroll.HorizontalScrollBarVisibility = presenting ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = presenting ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        UpdateCanvasBackground();

        // Presenting uses no gaps between pages, so the layout changes.
        int page = _currentPage;
        RebuildLayout();
        if (FitMode != FitMode.None)
        {
            ApplyFit();
        }

        GoToPage(page);
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Scrolls so a page (optionally a point on it, as a fraction from the top) is at the top of the view.</summary>
    /// <summary>Call after the viewer was hidden (e.g. by Organise): re-fits and redraws the pages in view.</summary>
    public void Redraw()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (FitMode != FitMode.None && _document != null)
            {
                ApplyFit();
            }

            UpdateVisibleRange(settled: true);
        });
    }

    public void GoToPage(int pageIndex, double? fractionFromTop = null)
    {
        if (_document == null || _document.PageCount == 0)
        {
            return;
        }

        pageIndex = Math.Clamp(pageIndex, 0, _document.PageCount - 1);
        if (_viewMode == ViewMode.SinglePage && (_layout == null || _layout[pageIndex] == null))
        {
            _currentPage = pageIndex;
            RebuildLayout();
            if (FitMode != FitMode.None)
            {
                ApplyFit();
            }
        }

        if (_layout?[pageIndex] is not { } rect)
        {
            return;
        }

        double zoom = TargetZoom;
        double y = fractionFromTop is double f ? rect.Y + (f * rect.Height) - 24 : rect.Y - _layout.Gap;
        double? x = null;
        if (rect.Width * zoom > _scroll.ViewportWidth)
        {
            x = fractionFromTop is null ? rect.X * zoom : null; // keep horizontal position for in-page jumps
        }
        else if (!IsHorizontallyVisible(rect.X, rect.Right, zoom))
        {
            x = CentredOffset(rect.X, rect.Right, zoom); // a narrow page beside a wide one: bring it into view
        }

        _scroll.ChangeView(x, Math.Max(0, y * zoom), (float)zoom, true);
        SetCurrentPage(pageIndex);
    }

    // Single page view only lays out the current page, so it steps by page number instead of by row.
    public void NextPage() => GoToPage(_viewMode == ViewMode.SinglePage || _layout == null ? _currentPage + 1 : _layout.StepPage(_currentPage, 1));

    public void PreviousPage() => GoToPage(_viewMode == ViewMode.SinglePage || _layout == null ? _currentPage - 1 : _layout.StepPage(_currentPage, -1));

    // ---------------------------------------------------------------- zoom

    public void SetZoom(double zoom)
    {
        FitMode = FitMode.None;
        ZoomAround(zoom, null);
    }

    public void ZoomIn() => SetZoom(Zoom.Step(_scroll.ZoomFactor, +1));

    public void ZoomOut() => SetZoom(Zoom.Step(_scroll.ZoomFactor, -1));

    public void SetFitMode(FitMode mode)
    {
        FitMode = mode;
        if (mode == FitMode.None)
        {
            ZoomAround(1.0, null);
        }
        else
        {
            ApplyFit();
            GoToPage(_currentPage);
        }

        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The value saved per file: 0 = fit width, -1 = fit page, otherwise the zoom factor.</summary>
    public double SavedZoom => FitMode switch { FitMode.Width => 0, FitMode.Page => -1, _ => _scroll.ZoomFactor };

    /// <summary>
    /// Puts keyboard focus on the pages so arrows and Page Up/Down scroll. Uses "pointer" focus so no
    /// focus rectangle appears on its own; pressing Tab to get here still shows the coral outline.
    /// </summary>
    public void FocusPages() => _scroll.Focus(FocusState.Pointer);

    private void ZoomAround(double zoom, Point? anchor)
    {
        zoom = Zoom.Clamp(zoom);
        double old = _scroll.ZoomFactor;
        _pendingZoom = zoom;
        var a = anchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);

        // Keep the content under the anchor point in place.
        double contentX = (_scroll.HorizontalOffset + a.X) / old;
        double contentY = (_scroll.VerticalOffset + a.Y) / old;
        _scroll.ChangeView(Math.Max(0, (contentX * zoom) - a.X), Math.Max(0, (contentY * zoom) - a.Y), (float)zoom, true);
    }

    private void ApplyFit()
    {
        if (_layout == null || _scroll.ViewportWidth <= 0)
        {
            return;
        }

        double zoom = FitMode == FitMode.Page ? FitForCurrentRow() : FitWidthForCurrentRow();
        _pendingZoom = zoom;

        // The scroll area is as wide as the widest page. When a narrower page is fitted, centre it,
        // otherwise a document with one wide page shows empty space beside the page being read.
        var row = _layout.RowPages(_currentPage).Select(p => _layout[p]).OfType<LayoutRect>().ToList();
        double? x = row.Count > 0 ? CentredOffset(row.Min(r => r.X), row.Max(r => r.Right), zoom) : null;
        _scroll.ChangeView(x, null, (float)zoom, true);
        _renderedZoom = 0; // force re-render at the new size
    }

    /// <summary>Horizontal scroll offset that puts content from <paramref name="left"/> to <paramref name="right"/> in the middle.</summary>
    private double CentredOffset(double left, double right, double zoom) =>
        Math.Max(0, (((left + right) / 2) * zoom) - (_scroll.ViewportWidth / 2));

    private bool IsHorizontallyVisible(double left, double right, double zoom) =>
        left * zoom >= _scroll.HorizontalOffset - 1 && right * zoom <= _scroll.HorizontalOffset + _scroll.ViewportWidth + 1;

    /// <summary>
    /// Fit width uses the page (or spread) being read, not the widest page in the file, so one
    /// poster-sized page doesn't shrink every normal page (same as Acrobat and Edge).
    /// </summary>
    private double FitWidthForCurrentRow()
    {
        var rows = _layout!.RowPages(_currentPage);
        double width = rows.Sum(p => _layout[p]?.Width ?? 0) + (_layout.Gap * (rows.Count - 1));
        return Zoom.FitWidth(_scroll.ViewportWidth - (2 * ScreenMargin), width);
    }

    private double FitForCurrentRow()
    {
        var rows = _layout!.RowPages(_currentPage);
        double width = rows.Sum(p => _layout[p]?.Width ?? 0) + (_layout.Gap * (rows.Count - 1));
        double height = rows.Max(p => _layout[p]?.Height ?? 0);
        double margin = _presenting ? 0 : ScreenMargin;
        return Zoom.FitPage(_scroll.ViewportWidth - (2 * margin), _scroll.ViewportHeight - (2 * margin), width, height, 0);
    }

    /// <summary>
    /// Breathing room left and right of a fitted page, in screen pixels (so it's the same at any zoom),
    /// wide enough that Windows' overlay scrollbar never sits on the page.
    /// </summary>
    private const double ScreenMargin = 32;

    private void OnViewportResized()
    {
        if (FitMode != FitMode.None && _document != null)
        {
            // Wait until layout has settled: ViewportWidth isn't updated yet when SizeChanged fires.
            int page = _currentPage;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (FitMode != FitMode.None && _document != null)
                {
                    ApplyFit();
                    GoToPage(page);
                }
            });
        }

        UpdateVisibleRange(settled: false);
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    // ---------------------------------------------------------------- layout and scrolling

    private void RebuildLayout()
    {
        ClearVisuals();
        if (_document == null)
        {
            _layout = null;
            _surface.Width = _surface.Height = 0;
            return;
        }

        var sizes = _document.PageSizes.Select(s => ((double)s.Width, (double)s.Height)).ToList();
        _layout = DocumentLayout.Compute(sizes, _viewMode, _currentPage, gap: _presenting ? 0 : 16);
        _surface.Width = _layout.Width;
        _surface.Height = _layout.Height;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            _pendingZoom = null; // the requested zoom has arrived
        }

        UpdateVisibleRange(settled: false);
        UpdateCurrentPageFromScroll();

        // Re-render at the new sharpness once scrolling/zooming stops.
        _settleTimer.Stop();
        _settleTimer.Start();

        if (!e.IsIntermediate && Math.Abs(_scroll.ZoomFactor - _renderedZoom) > 0.001)
        {
            // The user zoomed with Ctrl+wheel or pinch: it is no longer "fit".
            if (FitMode != FitMode.None && _layout != null && Math.Abs(_scroll.ZoomFactor - ExpectedFitZoom()) > 0.01)
            {
                FitMode = FitMode.None;
            }

            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double ExpectedFitZoom() =>
        FitMode == FitMode.Page ? FitForCurrentRow() : FitWidthForCurrentRow();

    private void UpdateCurrentPageFromScroll()
    {
        if (_layout == null || _viewMode == ViewMode.SinglePage)
        {
            return;
        }

        double zoom = _scroll.ZoomFactor;
        double probe = (_scroll.VerticalOffset + (_scroll.ViewportHeight * 0.3)) / zoom;
        if (_scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 1)
        {
            probe = _layout.Height; // scrolled to the very end: the last page is current
        }

        SetCurrentPage(_layout.PageAt(probe));
    }

    private void SetCurrentPage(int page)
    {
        if (page != _currentPage)
        {
            _currentPage = page;
            CurrentPageChanged?.Invoke(this, page);
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Moving to a monitor with different scaling: re-render at the new pixel density.
        if (Math.Abs(sender.RasterizationScale - _rasterScale) > 0.01)
        {
            _rasterScale = sender.RasterizationScale;
            _renderedZoom = 0;
            UpdateVisibleRange(settled: true);
        }
    }

    private void UpdateCanvasBackground()
    {
        _scroll.Background = _presenting
            ? new SolidColorBrush(Microsoft.UI.Colors.Black)
            : Services.ThemeResources.Brush(this, "PaperyCanvasBrush");
        _scroll.FocusVisualPrimaryBrush = Services.ThemeResources.Brush(this, "PaperyCoralBrush");
        foreach (var visual in _visuals.Values)
        {
            RefreshHighlights(visual);
        }
    }

    private void ClearVisuals()
    {
        foreach (var visual in _visuals.Values)
        {
            visual.Reset();
            _surface.Children.Remove(visual);
            _pool.Push(visual);
        }

        _visuals.Clear();
    }

    /// <summary>Helper so the ScrollViewer's accessible name can be set without a XAML file.</summary>
    private static class AutomationPropertiesHelper
    {
        public static readonly DependencyProperty NameProperty = Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty;
    }
}
