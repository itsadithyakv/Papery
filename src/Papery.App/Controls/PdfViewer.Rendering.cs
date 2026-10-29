using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Core.Storage;
using Papery.Pdf;
using Papery.Services;
using Windows.UI;

namespace Papery.Controls;

public sealed partial class PdfViewer
{
    // A whole-page bitmap up to ~6 megapixels (A4 at about 300%) is fine; above that, tiles.
    private const double MaxBasePixels = 6_000_000;
    private const int TileSize = 512;
    private const long BaseCacheBytes = 256L * 1024 * 1024;
    private const long TileCacheBytes = 160L * 1024 * 1024;

    private readonly BitmapCache<(int Page, int Width, ReadingMode Mode)> _baseCache = new(BaseCacheBytes);
    private readonly BitmapCache<(int Page, double Scale, int Column, int Row, ReadingMode Mode)> _tileCache = new(TileCacheBytes);
    private readonly Dictionary<int, Task<IReadOnlyList<NormalizedRect>>> _imageAreas = new();
    private double _rasterScale = 1.0;

    /// <summary>
    /// Makes sure every page near the viewport has a visual, and asks for renders.
    /// <paramref name="settled"/> = scrolling/zooming has stopped, so it's worth re-rendering
    /// at the exact sharpness and drawing tiles.
    /// </summary>
    private void UpdateVisibleRange(bool settled)
    {
        if (_document == null || _layout == null || _scroll.ViewportHeight <= 0)
        {
            return;
        }

        _rasterScale = XamlRoot?.RasterizationScale ?? _rasterScale;
        double zoom = _scroll.ZoomFactor;
        double top = _scroll.VerticalOffset / zoom;
        double height = _scroll.ViewportHeight / zoom;

        // Keep half a screen above and below ready, so normal scrolling never shows blanks.
        var wanted = _layout.PagesInView(top - (height / 2), top + height + (height / 2)).ToHashSet();
        foreach (int page in _visuals.Keys.Where(p => !wanted.Contains(p)).ToList())
        {
            var visual = _visuals[page];
            visual.Reset();
            _surface.Children.Remove(visual);
            _pool.Push(visual);
            _visuals.Remove(page);
        }

        foreach (int page in wanted)
        {
            if (!_visuals.TryGetValue(page, out var visual))
            {
                visual = _pool.Count > 0 ? _pool.Pop() : CreateVisual();
                var rect = _layout[page]!.Value;
                visual.PageIndex = page;
                visual.Width = rect.Width;
                visual.Height = rect.Height;
                visual.SetPaperColor(PaperColor());
                Canvas.SetLeft(visual, rect.X);
                Canvas.SetTop(visual, rect.Y);
                _surface.Children.Add(visual);
                _visuals[page] = visual;
                RefreshHighlights(visual);
            }

            bool onScreen = _layout[page]!.Value.IntersectsVertically(top, top + height);
            RenderPage(visual, zoom, onScreen, settled);
        }

        if (settled)
        {
            _renderedZoom = zoom;
        }
    }

    private PageVisual CreateVisual()
    {
        var visual = new PageVisual();
        AttachPointerHandlers(visual);
        return visual;
    }

    /// <summary>Pixels per layout DIP, rounded to steps of ~19% so tiny zoom changes reuse renders.</summary>
    private double RenderScale(double zoom) =>
        Math.Pow(2, Math.Round(Math.Log2(Math.Max(0.05, zoom * _rasterScale)) * 4) / 4);

    private void RenderPage(PageVisual visual, double zoom, bool onScreen, bool settled)
    {
        var rect = _layout![visual.PageIndex]!.Value;
        double scale = RenderScale(zoom);
        double fullW = rect.Width * scale, fullH = rect.Height * scale;
        bool tiled = fullW * fullH > MaxBasePixels;

        // Base image: the whole page, capped at MaxBasePixels when tiles take over the detail.
        double baseFactor = tiled ? Math.Sqrt(MaxBasePixels / 2 / (fullW * fullH)) : 1;
        int baseW = Math.Max(16, (int)Math.Round(fullW * baseFactor));
        int baseH = Math.Max(16, (int)Math.Round(fullH * baseFactor));

        if (visual.BaseWidth != baseW && visual.PendingWidth != baseW && (settled || visual.BaseWidth == 0))
        {
            if (_baseCache.TryGet((visual.PageIndex, baseW, _readingMode), out var cached))
            {
                visual.SetBase(cached, baseW);
            }
            else
            {
                if (visual.BaseWidth == 0)
                {
                    // Show any lower-resolution copy straight away while the sharp one renders.
                    var placeholder = _baseCache.FindBest(k => k.Page == visual.PageIndex && k.Mode == _readingMode);
                    if (placeholder != null)
                    {
                        visual.SetBase(placeholder, 1);
                    }
                }

                _ = RenderBaseAsync(visual, baseW, baseH, onScreen ? PdfWorkPriority.Visible : PdfWorkPriority.Nearby);
            }
        }

        if (!settled)
        {
            return;
        }

        if (tiled && onScreen)
        {
            UpdateTiles(visual, rect, scale, zoom);
        }
        else if (visual.Tiles.Count > 0)
        {
            visual.ClearTiles();
        }
    }

    private async Task RenderBaseAsync(PageVisual visual, int width, int height, PdfWorkPriority priority)
    {
        var token = visual.RestartRendering();
        visual.PendingWidth = width;
        int page = visual.PageIndex;
        var document = _document!;
        var mode = _readingMode;
        try
        {
            var areas = mode == ReadingMode.Light ? [] : await GetImageAreasAsync(document, page);
            var bitmap = await document.RenderPageAsync(page, width, height, PdfRenderOptions.Annotations, priority, token);
            if (mode != ReadingMode.Light)
            {
                await Task.Run(() => PageBitmaps.ApplyReadingMode(bitmap, mode, areas, width, height), token);
            }

            if (token.IsCancellationRequested || visual.PageIndex != page || document != _document)
            {
                return;
            }

            var image = PageBitmaps.ToWriteableBitmap(bitmap);
            _baseCache.Add((page, width, mode), image);
            visual.SetBase(image, width);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The document was closed while this page was rendering.
        }
        catch (Exception ex)
        {
            Log.Error($"Rendering page {page + 1} failed", ex);
            if (visual.PageIndex == page)
            {
                visual.ShowError();
            }
        }
        finally
        {
            if (visual.PendingWidth == width)
            {
                visual.PendingWidth = 0;
            }
        }
    }

    /// <summary>Draws sharp 512px tiles for just the visible part of a page at high zoom.</summary>
    private void UpdateTiles(PageVisual visual, Core.Viewing.LayoutRect rect, double scale, double zoom)
    {
        if (Math.Abs(visual.TileScale - scale) > 0.0001)
        {
            visual.ClearTiles();
            visual.TileScale = scale;
        }

        int fullW = (int)Math.Round(rect.Width * scale), fullH = (int)Math.Round(rect.Height * scale);

        // Visible part of the page, in page pixels.
        double left = (_scroll.HorizontalOffset / zoom) - rect.X, top = (_scroll.VerticalOffset / zoom) - rect.Y;
        double right = left + (_scroll.ViewportWidth / zoom), bottom = top + (_scroll.ViewportHeight / zoom);
        int c0 = Math.Max(0, (int)(left * scale / TileSize)), c1 = Math.Min((fullW - 1) / TileSize, (int)(right * scale / TileSize));
        int r0 = Math.Max(0, (int)(top * scale / TileSize)), r1 = Math.Min((fullH - 1) / TileSize, (int)(bottom * scale / TileSize));

        var needed = new HashSet<(int, int)>();
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                needed.Add((c, r));
            }
        }

        foreach (var key in visual.Tiles.Keys.Where(k => !needed.Contains(k)).ToList())
        {
            visual.RemoveTile(key);
        }

        foreach (var (c, r) in needed)
        {
            if (visual.Tiles.ContainsKey((c, r)))
            {
                continue;
            }

            int x = c * TileSize, y = r * TileSize;
            int w = Math.Min(TileSize, fullW - x), h = Math.Min(TileSize, fullH - y);
            var image = new Image { Stretch = Stretch.Fill, Width = w / scale, Height = h / scale };
            Canvas.SetLeft(image, x / scale);
            Canvas.SetTop(image, y / scale);
            visual.AddTile((c, r), image);

            if (_tileCache.TryGet((visual.PageIndex, scale, c, r, _readingMode), out var cached))
            {
                image.Source = cached;
            }
            else
            {
                _ = RenderTileAsync(visual, image, scale, c, r, fullW, fullH, new PixelRect(x, y, w, h));
            }
        }
    }

    private async Task RenderTileAsync(PageVisual visual, Image image, double scale, int column, int row, int fullW, int fullH, PixelRect region)
    {
        int page = visual.PageIndex;
        var token = visual.RenderCancellation.Token;
        var document = _document!;
        var mode = _readingMode;
        try
        {
            var areas = mode == ReadingMode.Light ? [] : await GetImageAreasAsync(document, page);
            var bitmap = await document.RenderRegionAsync(page, fullW, fullH, region, PdfRenderOptions.Annotations, PdfWorkPriority.Visible, token);
            if (mode != ReadingMode.Light)
            {
                await Task.Run(() => PageBitmaps.ApplyReadingMode(bitmap, mode, areas, fullW, fullH, region.X, region.Y), token);
            }

            if (token.IsCancellationRequested || visual.PageIndex != page || !visual.Tiles.ContainsValue(image))
            {
                return;
            }

            var source = PageBitmaps.ToWriteableBitmap(bitmap);
            _tileCache.Add((page, scale, column, row, mode), source);
            image.Source = source;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Rendering a tile of page {page + 1} failed", ex);
        }
    }

    private Task<IReadOnlyList<NormalizedRect>> GetImageAreasAsync(PdfDocument document, int page)
    {
        if (!_imageAreas.TryGetValue(page, out var task))
        {
            task = document.GetImageAreasAsync(page);
            _imageAreas[page] = task;
        }

        return task;
    }

    private Color PaperColor() => PageBitmaps.PaperColor(_readingMode);

    private void ClearCaches()
    {
        _baseCache.Clear();
        _tileCache.Clear();
        _imageAreas.Clear();
        _links.Clear();
        _renderedZoom = 0;
    }
}
