using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Papery.Controls;
using Papery.Core.Pages;
using Papery.Core.Storage;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Organise;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Papery.Views;

/// <summary>How Organise mode ended.</summary>
/// <param name="Saved">True when the open file itself was changed and must be reloaded.</param>
public sealed record OrganiseClosed(bool Saved);

/// <summary>
/// Organise mode: pages as cards. Every change is only a list in memory until Save, which
/// hands the list to the engine's safe compose; so Cancel, undo and a crash all leave the file alone.
/// </summary>
public sealed partial class OrganiseView : UserControl, IOrganiseActions
{
    private static readonly PdfPageSize A4 = new(595.28f, 841.89f);

    private readonly ObservableCollection<OrganisePage> _pages = [];
    private readonly Dictionary<int, OrganisePage> _everyPage = []; // including deleted ones, so undo can bring them back
    private readonly List<PdfInputFile> _sources = [];              // 0 is the open file
    private readonly List<PdfDocument> _sourceDocuments = [];       // for thumbnails; 0 belongs to the document view
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly BitmapCache<(int Source, int Page, int Width)> _cache = new(64L * 1024 * 1024);
    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly HashSet<OrganisePage> _withImages = [];
    private Snapshot _saved = Snapshot.Empty;
    private Snapshot? _dragStart;
    private PdfDocument _document = null!;
    private ReadingMode _readingMode;
    private Brush _paper = new SolidColorBrush(Microsoft.UI.Colors.White);
    private int _nextId;
    private CancellationTokenSource? _busy;
    private bool _closed;

    public OrganiseView()
    {
        InitializeComponent();
        Board.ItemsSource = _pages;
        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RefreshRealizedImages();

        // Hooked up here, not in XAML: setting Minimum there fires ValueChanged before this view is ready.
        SizeSlider.ValueChanged += OnSizeChanged;

        foreach (var (keys, label) in new (string[] Keys, string Label)[] { (["Del"], "remove"), (["Ctrl", "Z"], "undo"), (["Ctrl", "R"], "turn") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (string key in keys)
            {
                row.Children.Add(HomeView.Keycap(key));
            }

            row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] });
            HintRow.Children.Add(row);
        }
    }

    /// <summary>Raised when the user leaves Organise mode (Done, Esc, or after saving).</summary>
    public event EventHandler<OrganiseClosed>? Closed;

    /// <summary>Asks the window to open a file in a new tab (after "Save as a copy" or Extract).</summary>
    public event EventHandler<string>? OpenRequested;

    internal Func<nint> GetWindowHandle { get; set; } = () => 0;

    public bool HasChanges => !Current().Equals(_saved);

    public void Initialize(PdfDocument document, ReadingMode readingMode)
    {
        _document = document;
        _readingMode = readingMode;
        _paper = new SolidColorBrush(PageBitmaps.PaperColor(readingMode));
        _sources.Add(new PdfInputFile(document.FilePath, document.Password));
        _sourceDocuments.Add(document);
        for (int i = 0; i < document.PageCount; i++)
        {
            _pages.Add(NewPage(new PageRef(0, i), document, null, document.PageSizes[i]));
        }

        _saved = Current();
        AfterChange();
        Loaded += (_, _) => Board.Focus(FocusState.Programmatic);
    }

    /// <summary>Asks about unsaved changes. Returns false if the user wants to stay.</summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasChanges || XamlRoot == null)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Save your page changes?",
            Content = Dialogs.Wrap($"Your changes to \"{Path.GetFileName(_document.FilePath)}\" haven't been saved yet."),
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
        };
        return await Dialogs.ShowAsync(dialog, XamlRoot) switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    /// <summary>Closes the extra files opened for "add pages from another PDF".</summary>
    public async Task CloseAsync()
    {
        _busy?.Cancel();
        foreach (var page in _pages)
        {
            page.Loading?.Cancel();
        }

        foreach (var document in _sourceDocuments.Skip(1))
        {
            await document.DisposeAsync();
        }

        _sourceDocuments.Clear();
        _cache.Clear();
    }

    // ================================================================= state, undo and redo

    /// <summary>The board as (page id, turns) pairs: cheap to copy, compare and restore.</summary>
    private sealed record Snapshot((int Id, int Turns)[] Pages)
    {
        public static readonly Snapshot Empty = new([]);

        public bool Equals(Snapshot? other) => other != null && Pages.AsSpan().SequenceEqual(other.Pages);

        public override int GetHashCode() => Pages.Length;
    }

    private Snapshot Current() => new(_pages.Select(p => (p.Id, p.QuarterTurns)).ToArray());

    /// <summary>Call before every change.</summary>
    private void RememberForUndo()
    {
        _undo.Push(Current());
        _redo.Clear();
    }

    private void Restore(Snapshot snapshot)
    {
        // Move items rather than rebuild the list, so the board animates and keeps its scroll position.
        for (int i = 0; i < snapshot.Pages.Length; i++)
        {
            var (id, turns) = snapshot.Pages[i];
            var page = _everyPage[id];
            page.QuarterTurns = turns;
            int at = _pages.IndexOf(page);
            if (at < 0)
            {
                _pages.Insert(i, page);
            }
            else if (at != i)
            {
                _pages.Move(at, i);
            }
        }

        while (_pages.Count > snapshot.Pages.Length)
        {
            _pages.RemoveAt(_pages.Count - 1);
        }

        AfterChange();
    }

    private void Undo()
    {
        if (_undo.Count > 0 && _busy == null)
        {
            _redo.Push(Current());
            Restore(_undo.Pop());
        }
    }

    private void Redo()
    {
        if (_redo.Count > 0 && _busy == null)
        {
            _undo.Push(Current());
            Restore(_redo.Pop());
        }
    }

    private void AfterChange()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Position = i;
        }

        UpdateCommands();
    }

    private void UpdateCommands()
    {
        int selected = Board.SelectedItems.Count;
        bool any = selected > 0 && _busy == null;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = DuplicateButton.IsEnabled = ExtractButton.IsEnabled = any;
        DeleteButton.IsEnabled = any && selected < _pages.Count;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        SaveButton.IsEnabled = HasChanges && _busy == null;
        string pages = _pages.Count == 1 ? "1 page" : $"{_pages.Count:N0} pages";
        StatusText.Text = selected > 0 ? $"{pages}  ·  {selected:N0} selected" : pages;
    }

    private OrganisePage NewPage(PageRef reference, PdfDocument? document, string? fromFile, PdfPageSize size)
    {
        var page = new OrganisePage(_nextId++, reference, document, fromFile, size, this)
        {
            TileSize = SizeSlider.Value,
            Paper = _paper,
            ImageNeeded = p => _ = LoadImageAsync(p),
        };
        _everyPage[page.Id] = page;
        return page;
    }

    private List<OrganisePage> SelectedInOrder() =>
        Board.SelectedItems.OfType<OrganisePage>().OrderBy(p => p.Position).ToList();

    /// <summary>Where new pages go: after the last selected page, or at the end.</summary>
    private int InsertionIndex() => Board.SelectedItems.Count > 0 ? SelectedInOrder()[^1].Position + 1 : _pages.Count;

    // ================================================================= actions

    void IOrganiseActions.RotatePage(OrganisePage page, int quarterTurns) => Rotate([page], quarterTurns);

    void IOrganiseActions.DeletePage(OrganisePage page) => Delete([page]);

    private void Rotate(List<OrganisePage> pages, int quarterTurns)
    {
        if (pages.Count == 0 || _busy != null)
        {
            return;
        }

        RememberForUndo();
        foreach (var page in pages)
        {
            page.QuarterTurns = (((page.QuarterTurns + quarterTurns) % 4) + 4) % 4;
        }

        AfterChange();
    }

    private void Delete(List<OrganisePage> pages)
    {
        if (pages.Count == 0 || _busy != null)
        {
            return;
        }

        if (pages.Count >= _pages.Count)
        {
            Toaster.Show("A PDF needs at least one page", "");
            return;
        }

        RememberForUndo();
        foreach (var page in pages)
        {
            _pages.Remove(page);
        }

        AfterChange();
        Toaster.Show(pages.Count == 1 ? "Page removed" : $"{pages.Count} pages removed", "", "Undo", Undo);
    }

    private void Duplicate()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0 || _busy != null)
        {
            return;
        }

        RememberForUndo();
        var copies = new List<OrganisePage>();
        foreach (var original in selected)
        {
            var copy = NewPage(original.Reference, original.Document, original.FromFile, original.Size);
            copy.QuarterTurns = original.QuarterTurns;
            _pages.Insert(original.Position + 1 + copies.Count, copy);
            copies.Add(copy);
        }

        AfterChange();

        // The copies become the selection, so "add blank page" or another action continues from them.
        Board.SelectedItems.Clear();
        foreach (var copy in copies)
        {
            Board.SelectedItems.Add(copy);
        }
    }

    private void InsertBlank()
    {
        if (_busy != null)
        {
            return;
        }

        // Same size and orientation as the page it follows, so it doesn't stand out when printed.
        int index = InsertionIndex();
        var size = index > 0 ? _pages[index - 1].TurnedSize : _pages.Count > 0 ? _pages[0].TurnedSize : A4;
        RememberForUndo();
        var blank = NewPage(PageRef.Blank(size), null, null, size);
        _pages.Insert(index, blank);
        AfterChange();
        Board.ScrollIntoView(blank);
    }

    private async Task InsertFilesAsync(IReadOnlyList<string> paths, int index)
    {
        var added = new List<OrganisePage>();
        foreach (string path in paths.Where(DropFiles.IsPdf))
        {
            var document = await DocumentOpener.OpenAsync(path, XamlRoot);
            if (document == null)
            {
                continue;
            }

            if (!document.CanAssemble)
            {
                await document.DisposeAsync();
                var (title, message) = EditErrors.Explain(new PdfEditException(PdfEditError.NotAllowed, path));
                await Dialogs.ShowMessageAsync(XamlRoot, title, message, MascotMood.Oops);
                continue;
            }

            int source = _sources.Count;
            _sources.Add(new PdfInputFile(path, document.Password));
            _sourceDocuments.Add(document);
            string name = Path.GetFileName(path);
            for (int i = 0; i < document.PageCount; i++)
            {
                added.Add(NewPage(new PageRef(source, i), document, name, document.PageSizes[i]));
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        RememberForUndo();
        index = Math.Clamp(index, 0, _pages.Count);
        foreach (var page in added)
        {
            _pages.Insert(index++, page);
        }

        AfterChange();
        Board.SelectedItems.Clear();
        Board.ScrollIntoView(added[0]);
        Toaster.Show(added.Count == 1 ? "1 page added" : $"{added.Count:N0} pages added", "", "Undo", Undo);
    }

    // ================================================================= saving

    private PdfComposeRequest Request(IEnumerable<OrganisePage> pages, bool inPlace) =>
        new(_sources, pages.Select(p => p.Current).ToArray(), inPlace ? 0 : null);

    /// <summary>Saves over the open file. Returns true when saved (Organise then closes).</summary>
    private async Task<bool> SaveAsync()
    {
        if (!HasChanges)
        {
            Close(saved: false);
            return true;
        }

        var result = await RunAsync("Saving", ct => PdfEngine.Shared.ComposeAsync(Request(_pages, inPlace: true), _document.FilePath, Progress(), ct));
        if (result == null)
        {
            return false;
        }

        _saved = Current();
        Close(saved: true);
        return true;
    }

    private async Task SaveCopyAsync()
    {
        string name = Path.GetFileNameWithoutExtension(_document.FilePath) + " (edited).pdf";
        string? path = await Pickers.PickSavePdfAsync(GetWindowHandle(), name, _document.FilePath);
        if (path == null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync(); // they picked the open file itself: that's a normal save
            return;
        }

        var result = await RunAsync("Saving a copy", ct => PdfEngine.Shared.ComposeAsync(Request(_pages, inPlace: true), path, Progress(), ct), path);
        if (result != null)
        {
            Toaster.Show($"Saved a copy: {Path.GetFileName(path)}", "", "Open", () => OpenRequested?.Invoke(this, path));
        }
    }

    private async Task ExtractAsync()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0)
        {
            return;
        }

        string range = PageRanges.Format(selected.Select(p => p.Position));
        string name = $"{Path.GetFileNameWithoutExtension(_document.FilePath)} (pages {range})";
        string? path = await Pickers.PickSavePdfAsync(GetWindowHandle(), SplitPlanner.SafeFileName(name, 100) + ".pdf", _document.FilePath);
        if (path == null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Pick a different name", "Extracted pages go into a new file, so the open file stays as it is.");
            return;
        }

        var request = new PdfComposeRequest(_sources, selected.Select(p => p.Current).ToArray());
        var result = await RunAsync("Saving pages", ct => PdfEngine.Shared.ComposeAsync(request, path, Progress(), ct), path);
        if (result != null)
        {
            Toaster.Show(selected.Count == 1 ? "Saved 1 page as a new PDF" : $"Saved {selected.Count:N0} pages as a new PDF", "", "Open", () => OpenRequested?.Invoke(this, path));
        }
    }

    private Progress<double> Progress() => new(v => BusyProgress.Value = v);

    /// <summary>
    /// Runs a save with the busy screen up. Problems are explained here; returns null on failure or cancel.
    /// </summary>
    /// <param name="newFile">A file the save picker created, removed again if the save doesn't finish.</param>
    private async Task<T?> RunAsync<T>(string what, Func<CancellationToken, Task<T>> work, string? newFile = null)
        where T : class
    {
        using var cts = new CancellationTokenSource();
        _busy = cts;
        BusyText.Text = what;
        BusyProgress.Value = 0;
        BusyMascot.Mood = MascotMood.Happy;
        BusyOverlay.Visibility = Visibility.Visible;
        Motion.Entrance(BusyOverlay, 0, 0);
        UpdateCommands();
        try
        {
            return await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
            return null;
        }
        finally
        {
            if (newFile != null)
            {
                Pickers.RemoveIfEmpty(newFile);
            }

            _busy = null;
            BusyOverlay.Visibility = Visibility.Collapsed;
            UpdateCommands();
        }
    }

    // ================================================================= thumbnails

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not OrganisePage page)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            // Images are not dropped here: while dragging, a page's old tile is recycled after its
            // new one appears. Off-screen images are trimmed in TrimImages instead.
            page.Loading?.Cancel();
            return;
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, page.AccessibleName);
        if (page.Image == null)
        {
            _ = LoadImageAsync(page);
        }
    }

    private int ImageWidth(OrganisePage page)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double fit = (page.TileSize - 20) / Math.Max(page.Size.Width, page.Size.Height);
        return Math.Max(16, (int)(page.Size.Width * fit * scale));
    }

    private async Task LoadImageAsync(OrganisePage page)
    {
        if (page.IsBlank || page.Document is not { } document)
        {
            return;
        }

        int width = ImageWidth(page);
        var key = (page.Reference.Source, page.Reference.Page, width);
        if (_cache.TryGet(key, out var cached))
        {
            page.Image = cached;
            page.RenderedWidth = width;
            return;
        }

        if (page.Loading is { IsCancellationRequested: false })
        {
            return; // already on its way
        }

        var cts = new CancellationTokenSource();
        page.Loading = cts;
        int index = page.Reference.Page;
        int height = Math.Max(16, (int)(width * page.Size.Height / page.Size.Width));
        try
        {
            var bitmap = await document.RenderPageAsync(index, width, height, PdfRenderOptions.Annotations, PdfWorkPriority.Nearby, cts.Token);
            if (_readingMode != ReadingMode.Light)
            {
                var areas = await document.GetImageAreasAsync(index, cts.Token);
                var mode = _readingMode;
                await Task.Run(() => PageBitmaps.ApplyReadingMode(bitmap, mode, areas, width, height), cts.Token);
            }

            WriteableBitmap image = PageBitmaps.ToWriteableBitmap(bitmap);
            _cache.Add(key, image);
            if (!cts.IsCancellationRequested)
            {
                page.Image = image;
                page.RenderedWidth = width;
                _withImages.Add(page);
                TrimImages();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Organise thumbnail for page {index + 1} failed", ex);
        }
        finally
        {
            if (page.Loading == cts)
            {
                page.Loading = null;
            }
        }
    }

    /// <summary>
    /// Keeps memory flat on long documents: once many pages hold a picture, drop the pictures
    /// of pages that aren't on screen (the cache may still have them, so scrolling back is quick).
    /// </summary>
    private void TrimImages()
    {
        const int Keep = 240;
        if (_withImages.Count <= Keep)
        {
            return;
        }

        foreach (var page in _withImages.ToList())
        {
            if (Board.ContainerFromItem(page) == null)
            {
                page.Image = null;
                page.RenderedWidth = 0;
                _withImages.Remove(page);
            }
        }
    }

    /// <summary>After resizing, sharpen the pages on screen (others render when they scroll in).</summary>
    private void RefreshRealizedImages()
    {
        foreach (var page in _pages)
        {
            if (Board.ContainerFromItem(page) != null && page.RenderedWidth < ImageWidth(page))
            {
                _ = LoadImageAsync(page);
            }
        }
    }

    // ================================================================= dragging

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e) => _dragStart = Current();

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_dragStart is { } before && !before.Equals(Current()))
        {
            _undo.Push(before);
            _redo.Clear();
        }

        _dragStart = null;
        AfterChange();
    }

    private void OnBoardDragOver(object sender, DragEventArgs e)
    {
        // Pages being reordered are handled by the board itself; this is for PDFs dropped from Explorer.
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && _busy == null)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add these pages here";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private void OnBoardDrop(object sender, DragEventArgs e) => Guard.Run(async () =>
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || _busy != null)
        {
            return;
        }

        var point = e.GetPosition(Board);
        var deferral = e.GetDeferral();
        IReadOnlyList<string> paths;
        try
        {
            paths = await DropFiles.GetPathsAsync(e);
        }
        finally
        {
            deferral.Complete();
        }

        await InsertFilesAsync(paths, DropIndex(point));
    }, "OnBoardDrop");

    /// <summary>The position between pages nearest to where the files were dropped.</summary>
    private int DropIndex(Windows.Foundation.Point point)
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            if (Board.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            var bounds = container.TransformToVisual(Board).TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            bool aboveRow = point.Y < bounds.Top;
            bool inRowBeforeCentre = point.Y <= bounds.Bottom && point.X < bounds.X + (bounds.Width / 2);
            if (aboveRow || inRowBeforeCentre)
            {
                return i;
            }
        }

        return _pages.Count;
    }

    // ================================================================= events

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();

    private void OnSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        foreach (var page in _pages)
        {
            page.TileSize = e.NewValue;
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnBoardWheel(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl + wheel resizes the pages, like zooming in the viewer.
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(Board).Properties.MouseWheelDelta;
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + (delta > 0 ? 20 : -20), SizeSlider.Minimum, SizeSlider.Maximum);
        e.Handled = true;
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(SelectedInOrder(), -1);

    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(SelectedInOrder(), 1);

    private void OnDelete(object sender, RoutedEventArgs e) => Delete(SelectedInOrder());

    private void OnDuplicate(object sender, RoutedEventArgs e) => Duplicate();

    private void OnInsertBlank(object sender, RoutedEventArgs e) => InsertBlank();

    private void OnInsertFile(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        var paths = await Pickers.PickPdfsAsync(GetWindowHandle());
        if (paths.Count > 0)
        {
            await InsertFilesAsync(paths, InsertionIndex());
        }
    }, "OnInsertFile");

    private void OnExtract(object sender, RoutedEventArgs e) => Guard.Run(() => ExtractAsync(), "OnExtract");

    private void OnSplit(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        if (HasChanges)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Save first, then split", "Split works on the file as it's saved. Save your page changes first, then split.");
            return;
        }

        if (await SplitDialog.ShowAsync(_document, XamlRoot, GetWindowHandle()) is { Files.Count: > 0 } outcome)
        {
            Toaster.Show(SplitDialog.Describe(outcome), "", "Show", () => Shell.ShowInFolder(outcome.Files[0]));
        }
    }, "OnSplit");

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void OnSaveClick(object sender, RoutedEventArgs e) => Guard.Run(() => SaveAsync(), "OnSaveClick");

    private void OnSaveCopyClick(object sender, RoutedEventArgs e) => Guard.Run(() => SaveCopyAsync(), "OnSaveCopyClick");

    private void OnDone(object sender, RoutedEventArgs e) => Guard.Run(() => LeaveAsync(), "OnDone");

    private void OnBusyCancel(object sender, RoutedEventArgs e) => _busy?.Cancel();

    private async Task LeaveAsync()
    {
        if (_busy == null && await ConfirmLeaveAsync())
        {
            Close(saved: false); // no-op if choosing "Save" in the prompt already closed it
        }
    }

    private void Close(bool saved)
    {
        if (!_closed)
        {
            _closed = true;
            Closed?.Invoke(this, new OrganiseClosed(saved));
        }
    }

    // Shortcuts. Delete and Ctrl+A are left to text boxes when one has focus (none on this board today).
    private void OnDeleteShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Delete(SelectedInOrder()); a.Handled = true; }

    private void OnUndoShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Undo(); a.Handled = true; }

    private void OnRedoShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Redo(); a.Handled = true; }

    private void OnRotateRightShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Rotate(SelectedInOrder(), 1); a.Handled = true; }

    private void OnRotateLeftShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Rotate(SelectedInOrder(), -1); a.Handled = true; }

    private void OnDuplicateShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Duplicate(); a.Handled = true; }

    private void OnSaveShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Guard.Run(async () =>
    {
        a.Handled = true;
        if (HasChanges && _busy == null)
        {
            await SaveAsync();
        }
    }, "OnSaveShortcut");

    private void OnEscapeShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => Guard.Run(async () =>
    {
        a.Handled = true;
        if (_busy != null)
        {
            _busy.Cancel();
            return;
        }

        await LeaveAsync();
    }, "OnEscapeShortcut");
}
