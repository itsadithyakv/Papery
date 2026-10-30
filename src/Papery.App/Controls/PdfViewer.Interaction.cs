using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Viewing;
using Papery.Pdf;
using Papery.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Papery.Controls;

public sealed partial class PdfViewer
{
    private static readonly SolidColorBrush SelectionBrush = new(Color.FromArgb(0x55, 0x00, 0x78, 0xD7));

    private readonly Dictionary<int, Task<IReadOnlyList<PdfLink>>> _links = new();
    private Dictionary<int, List<PdfSearchHit>> _hitsByPage = new();
    private PdfSearchHit? _currentHit;
    private PdfTextSelection? _selection;

    // Pointer state for a press-drag-release on a page.
    private PageVisual? _pressVisual;
    private Point _pressPoint;
    private bool _dragged;
    private PdfLink? _pressedLink;
    private int _anchorChar = -1;
    private bool _selectionBusy;
    private Point? _pendingSelectionPoint;
    private bool _hoverBusy;

    /// <summary>A link was clicked. Internal links are followed by the viewer's owner via <see cref="GoToPage"/>.</summary>
    public event EventHandler<PdfLink>? LinkClicked;

    public event EventHandler? SelectionChanged;

    public PdfTextSelection? Selection => _selection;

    // ---------------------------------------------------------------- search highlights

    public void SetSearchResults(IEnumerable<PdfSearchHit> hits, PdfSearchHit? current)
    {
        _hitsByPage = hits.GroupBy(h => h.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        _currentHit = current;
        foreach (var visual in _visuals.Values)
        {
            RefreshHighlights(visual);
        }
    }

    /// <summary>Highlights one hit as "current" and scrolls to it if it isn't already in view.</summary>
    public void ShowSearchHit(PdfSearchHit hit)
    {
        _currentHit = hit;
        foreach (var visual in _visuals.Values)
        {
            RefreshHighlights(visual);
        }

        if (_layout?[hit.PageIndex] is { } rect && hit.Areas.Count > 0)
        {
            double zoom = _scroll.ZoomFactor;
            double y = rect.Y + (hit.Areas[0].Y * rect.Height);
            double top = _scroll.VerticalOffset / zoom, bottom = top + (_scroll.ViewportHeight / zoom);
            if (y < top + 20 || y > bottom - 40)
            {
                GoToPage(hit.PageIndex, Math.Max(0, hit.Areas[0].Y - 0.1));
            }
        }
        else
        {
            GoToPage(hit.PageIndex);
        }
    }

    // ---------------------------------------------------------------- selection

    public void ClearSelection()
    {
        if (_selection == null)
        {
            return;
        }

        var page = _selection.PageIndex;
        _selection = null;
        if (_visuals.TryGetValue(page, out var visual))
        {
            RefreshHighlights(visual);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SelectAllOnCurrentPageAsync()
    {
        if (_document == null)
        {
            return;
        }

        string text = await _document.GetPageTextAsync(_currentPage);
        if (text.Length > 0)
        {
            SetSelection(await _document.GetSelectionAsync(_currentPage, 0, text.Length - 1));
        }
    }

    private void SetSelection(PdfTextSelection? selection)
    {
        int? oldPage = _selection?.PageIndex;
        _selection = selection is { Length: > 0 } ? selection : null;
        foreach (int page in new[] { oldPage, _selection?.PageIndex }.OfType<int>().Distinct())
        {
            if (_visuals.TryGetValue(page, out var visual))
            {
                RefreshHighlights(visual);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshHighlights(PageVisual visual)
    {
        var highlights = new List<(NormalizedRect, Brush)>();
        if (_hitsByPage.TryGetValue(visual.PageIndex, out var hits))
        {
            var hitBrush = ThemeResources.Brush(this, "PaperySearchHitBrush");
            var currentBrush = ThemeResources.Brush(this, "PaperySearchCurrentBrush");
            foreach (var hit in hits)
            {
                var brush = ReferenceEquals(hit, _currentHit) ? currentBrush : hitBrush;
                highlights.AddRange(hit.Areas.Select(a => (a, brush)));
            }
        }

        if (_selection?.PageIndex == visual.PageIndex)
        {
            highlights.AddRange(_selection.Areas.Select(a => (a, (Brush)SelectionBrush)));
        }

        visual.SetHighlights(highlights);
    }

    // ---------------------------------------------------------------- pointer

    private void InitializeInteraction()
    {
        _scroll.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void AttachPointerHandlers(PageVisual visual)
    {
        visual.PointerPressed += OnPagePointerPressed;
        visual.PointerMoved += OnPagePointerMoved;
        visual.PointerReleased += OnPagePointerReleased;
        visual.PointerCaptureLost += (_, _) => _pressVisual = null;
        visual.DoubleTapped += OnPageDoubleTapped;
    }

    private static (double X, double Y) Normalize(PageVisual visual, Point p) =>
        (Math.Clamp(p.X / visual.Width, 0, 1), Math.Clamp(p.Y / visual.Height, 0, 1));

    private async void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var visual = (PageVisual)sender;
        var point = e.GetCurrentPoint(visual);
        if (_document == null || point.PointerDeviceType == PointerDeviceType.Touch || !point.Properties.IsLeftButtonPressed)
        {
            return; // touch scrolls; right-click opens the context menu
        }

        _scroll.Focus(FocusState.Pointer);
        if (ToolPressed(visual, e))
        {
            return;
        }

        _pressVisual = visual;
        _pressPoint = point.Position;
        _dragged = false;
        _anchorChar = -1;
        visual.CapturePointer(e.Pointer);
        e.Handled = true;

        var (x, y) = Normalize(visual, point.Position);
        int page = visual.PageIndex;
        try
        {
            // With a marking tool, a press on a link starts marking its words instead of following it.
            _pressedLink = MarkTextOnSelect ? null : await LinkAtAsync(page, x, y);
            if (_pressedLink == null)
            {
                _anchorChar = await _document.GetCharIndexAtAsync(page, x, y);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
            _pressedLink = null;
        }
    }

    private async void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var visual = (PageVisual)sender;
        var point = e.GetCurrentPoint(visual);
        if (_document == null || point.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        if (ToolMoved(visual, e))
        {
            return;
        }

        if (_pressVisual == visual && point.Properties.IsLeftButtonPressed)
        {
            if (!_dragged && Math.Abs(point.Position.X - _pressPoint.X) + Math.Abs(point.Position.Y - _pressPoint.Y) > 4)
            {
                _dragged = true;
            }

            if (_dragged && _anchorChar >= 0)
            {
                _pendingSelectionPoint = point.Position;
                await UpdateDragSelectionAsync(visual);
            }

            return;
        }

        await UpdateHoverCursorAsync(visual, point.Position);
    }

    private void OnPagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var visual = (PageVisual)sender;
        if (ToolReleased(visual, e) || _pressVisual != visual)
        {
            return;
        }

        visual.ReleasePointerCapture(e.Pointer);
        _pressVisual = null;
        if (_dragged && MarkTextOnSelect && _selection is { } marked)
        {
            TextMarked?.Invoke(this, marked);
            ClearSelection();
        }

        if (!_dragged)
        {
            if (_pressedLink != null)
            {
                LinkClicked?.Invoke(this, _pressedLink);
            }
            else
            {
                ClearSelection();
            }
        }
    }

    private async void OnPageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var visual = (PageVisual)sender;
        if (_document == null)
        {
            return;
        }

        var (x, y) = Normalize(visual, e.GetPosition(visual));
        try
        {
            int index = await _document.GetCharIndexAtAsync(visual.PageIndex, x, y);
            if (index >= 0)
            {
                SetSelection(await _document.GetWordAtAsync(visual.PageIndex, index));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
        }
    }

    /// <summary>Extends the selection to the latest pointer position; only one query in flight at a time.</summary>
    private async Task UpdateDragSelectionAsync(PageVisual visual)
    {
        if (_selectionBusy)
        {
            return;
        }

        _selectionBusy = true;
        try
        {
            while (_pendingSelectionPoint is Point p && _document != null && _anchorChar >= 0)
            {
                _pendingSelectionPoint = null;
                var (x, y) = Normalize(visual, p);
                int index = await _document.GetCharIndexAtAsync(visual.PageIndex, x, y);
                if (index >= 0)
                {
                    SetSelection(await _document.GetSelectionAsync(visual.PageIndex, _anchorChar, index));
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            _selectionBusy = false;
        }
    }

    private async Task UpdateHoverCursorAsync(PageVisual visual, Point position)
    {
        if (_hoverBusy || _document == null)
        {
            return;
        }

        _hoverBusy = true;
        try
        {
            var (x, y) = Normalize(visual, position);
            if (await LinkAtAsync(visual.PageIndex, x, y) != null)
            {
                visual.SetCursor(InputSystemCursorShape.Hand);
            }
            else
            {
                bool overText = await _document.GetCharIndexAtAsync(visual.PageIndex, x, y) >= 0;
                visual.SetCursor(overText ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Arrow);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            _hoverBusy = false;
        }
    }

    private async Task<PdfLink?> LinkAtAsync(int page, double x, double y)
    {
        if (_document == null)
        {
            return null;
        }

        if (!_links.TryGetValue(page, out var task))
        {
            task = _document.GetLinksAsync(page);
            _links[page] = task;
        }

        try
        {
            var links = await task;
            return links.FirstOrDefault(l => l.Area.Contains(x, y));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Reading links on page {page + 1} failed", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- keyboard

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_document == null || IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
        {
            return;
        }

        bool paged = _viewMode == ViewMode.SinglePage || _presenting;
        bool noHorizontalScroll = _scroll.ScrollableWidth < 1;
        switch (e.Key)
        {
            case VirtualKey.Home:
                GoToPage(0);
                break;
            case VirtualKey.End:
                GoToPage(_document.PageCount - 1);
                break;
            case VirtualKey.PageDown or VirtualKey.Space when paged && AtBottom():
            case VirtualKey.Right or VirtualKey.Down when paged && noHorizontalScroll && AtBottom():
            case VirtualKey.Right when !paged && noHorizontalScroll && _viewMode != ViewMode.Continuous:
                NextPage();
                break;
            case VirtualKey.PageUp when paged && AtTop():
            case VirtualKey.Left or VirtualKey.Up when paged && noHorizontalScroll && AtTop():
            case VirtualKey.Left when !paged && noHorizontalScroll && _viewMode != ViewMode.Continuous:
                PreviousPage();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private bool AtBottom() => _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1;

    private bool AtTop() => _scroll.VerticalOffset <= 1;

    private static bool IsModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
