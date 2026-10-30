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
    private sealed record Snapshot((int Id, int Turns, PageGeometry? Geometry)[] Pages)
    {
        public static readonly Snapshot Empty = new([]);

        public bool Equals(Snapshot? other) => other != null && Pages.AsSpan().SequenceEqual(other.Pages);

        public override int GetHashCode() => Pages.Length;
    }

    private Snapshot Current() => new(_pages.Select(p => (p.Id, p.QuarterTurns, p.Geometry)).ToArray());

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
            var (id, turns, geometry) = snapshot.Pages[i];
            var page = _everyPage[id];
            page.QuarterTurns = turns;
            page.Geometry = geometry;
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
            copy.Geometry = original.Geometry;
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

    /// <param name="size">A paper size, or null for the same size as the page it follows.</param>
    private void InsertBlank(PdfPageSize? size = null)
    {
        if (_busy != null)
        {
            return;
        }

        // By default the same size and orientation as the page it follows, so it doesn't stand out when printed.
        int index = InsertionIndex();
        size ??= index > 0 ? _pages[index - 1].TurnedSize : _pages.Count > 0 ? _pages[0].TurnedSize : A4;
        RememberForUndo();
        var blank = NewPage(PageRef.Blank(size.Value), null, null, size.Value);
        _pages.Insert(index, blank);
        AfterChange();
        Board.ScrollIntoView(blank);
    }

    private async Task InsertFilesAsync(IReadOnlyList<string> paths, int index)
    {
        var added = new List<OrganisePage>();
        foreach (string path in paths.Where(DropFiles.IsPdf))
        {
            if (await OpenSourceAsync(path) is not { } opened)
            {
                continue;
            }

            var (source, document) = opened;
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

    /// <summary>Moves the selected pages, keeping their order, to the start or the end.</summary>
    private void MoveSelected(bool toStart)
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0 || selected.Count == _pages.Count || _busy != null)
        {
            return;
        }

        RememberForUndo();
        for (int i = 0; i < selected.Count; i++)
        {
            int from = _pages.IndexOf(selected[i]);
            int to = toStart ? i : _pages.Count - selected.Count + i;
            _pages.Move(from, to);
        }

        AfterChange();
        Board.ScrollIntoView(selected[0]);
    }

    /// <summary>Reverses the selected pages among themselves, or the whole document when fewer than two are selected.</summary>
    private void Reverse()
    {
        var pages = Board.SelectedItems.Count > 1 ? SelectedInOrder() : _pages.ToList();
        if (pages.Count < 2 || _busy != null)
        {
            return;
        }

        RememberForUndo();
        var slots = pages.Select(p => p.Position).ToList(); // the same places, filled in reverse
        var reversed = Enumerable.Reverse(pages).ToList();
        for (int i = 0; i < slots.Count; i++)
        {
            _pages.Move(_pages.IndexOf(reversed[i]), slots[i]);
        }

        AfterChange();
    }

    /// <summary>Swaps the selected pages for pages of another PDF, starting at a page the user picks.</summary>
    private async Task ReplaceAsync()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0 || _busy != null)
        {
            return;
        }

        var paths = await Pickers.PickPdfsAsync(GetWindowHandle(), multiple: false);
        if (paths.Count == 0 || await OpenSourceAsync(paths[0]) is not { } added)
        {
            return;
        }

        var (source, document) = added;
        int first = 0;
        if (document.PageCount > 1)
        {
            var box = new NumberBox { Minimum = 1, Maximum = document.PageCount, Value = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160 };
            var dialog = new ContentDialog
            {
                Title = selected.Count == 1 ? "Which page should go in?" : $"Which page should the {selected.Count} new pages start from?",
                Content = box,
                PrimaryButtonText = "Replace",
                CloseButtonText = "Cancel",
            };
            if (await Dialogs.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary || double.IsNaN(box.Value))
            {
                return;
            }

            first = (int)box.Value - 1;
        }

        string name = Path.GetFileName(paths[0]);
        RememberForUndo();
        int replaced = 0;
        foreach (var old in selected)
        {
            int page = first + replaced;
            if (page >= document.PageCount)
            {
                break; // the other file ran out of pages; the rest stay as they were
            }

            _pages[_pages.IndexOf(old)] = NewPage(new PageRef(source, page), document, name, document.PageSizes[page]);
            replaced++;
        }

        AfterChange();
        Toaster.Show(replaced == 1 ? "1 page replaced" : $"{replaced} pages replaced", "\uE8AB", "Undo", Undo);
    }

    /// <summary>Opens another PDF as a page source (asking for its password if needed). Null if it can't be used.</summary>
    private async Task<(int Source, PdfDocument Document)?> OpenSourceAsync(string path)
    {
        var document = await DocumentOpener.OpenAsync(path, XamlRoot);
        if (document == null)
        {
            return null;
        }

        if (!document.CanAssemble)
        {
            await document.DisposeAsync();
            var (title, message) = EditErrors.Explain(new PdfEditException(PdfEditError.NotAllowed, path));
            await Dialogs.ShowMessageAsync(XamlRoot, title, message, MascotMood.Oops);
            return null;
        }

        _sources.Add(new PdfInputFile(path, document.Password));
        _sourceDocuments.Add(document);
        return (_sources.Count - 1, document);
    }

    // ================================================================= shape: crop, paper size, straighten

    /// <summary>The selected pages, or every page when none are selected.</summary>
    private List<OrganisePage> SelectedOrAll() => Board.SelectedItems.Count > 0 ? SelectedInOrder() : _pages.ToList();

    private async Task OpenCropAsync()
    {
        var selected = SelectedInOrder();
        var page = selected.FirstOrDefault() ?? _pages.FirstOrDefault();
        if (page == null || _busy != null)
        {
            return;
        }

        var editor = new CropEditor();
        Grid.SetRowSpan(editor, 3);
        editor.Closed += (_, choice) =>
        {
            Root.Children.Remove(editor);
            Board.Focus(FocusState.Programmatic);
            if (choice != null)
            {
                ApplyCrop(choice, page, selected);
            }
        };
        Root.Children.Add(editor);
        Motion.Entrance(editor, 0, 8);
        editor.Focus(FocusState.Programmatic);
        await editor.ShowAsync(page, selected.Count);
    }

    private void ApplyCrop(CropChoice choice, OrganisePage current, List<OrganisePage> selected)
    {
        // Page numbers count from 1, so odd pages sit at even positions.
        List<OrganisePage> targets = choice.Scope switch
        {
            CropScope.Selected => selected,
            CropScope.All => _pages.ToList(),
            CropScope.Odd => _pages.Where(p => p.Position % 2 == 0).ToList(),
            CropScope.Even => _pages.Where(p => p.Position % 2 == 1).ToList(),
            _ => [current],
        };
        if (targets.Count == 0)
        {
            return;
        }

        RememberForUndo();
        foreach (var page in targets)
        {
            page.Geometry = Normalize((page.Geometry ?? new PageGeometry()) with { Crop = choice.Crop });
        }

        AfterChange();
        string what = choice.Crop == null ? "Crop removed" : "Cropped";
        Toaster.Show(targets.Count == 1 ? $"{what}" : $"{what} on {targets.Count:N0} pages", "\uE7A8", "Undo", Undo);
    }

    private async Task PageSizeAsync()
    {
        var targets = SelectedOrAll();
        if (targets.Count == 0 || _busy != null)
        {
            return;
        }

        if (await PageSizeDialog.ShowAsync(XamlRoot, targets[0], targets.Count) is not { } choice)
        {
            return;
        }

        RememberForUndo();
        foreach (var page in targets)
        {
            page.Geometry = PageSizeDialog.Apply(page.Geometry, choice, page.TurnedSize);
        }

        AfterChange();
        Toaster.Show(targets.Count == 1 ? "Page size changed" : $"Page size changed on {targets.Count:N0} pages", "\uE7C3", "Undo", Undo);
    }

    /// <summary>Measures how crooked each page is (from its picture) and turns it straight.</summary>
    private async Task StraightenAsync()
    {
        var targets = SelectedOrAll().Where(p => !p.IsBlank).ToList();
        if (targets.Count == 0 || _busy != null)
        {
            return;
        }

        var angles = await RunAsync("Straightening", async ct =>
        {
            var found = new Dictionary<OrganisePage, double>();
            for (int i = 0; i < targets.Count; i++)
            {
                var page = targets[i];
                double scale = 1400 / Math.Max(page.Size.Width, page.Size.Height);
                int w = Math.Max(16, (int)(page.Size.Width * scale)), h = Math.Max(16, (int)(page.Size.Height * scale));
                var bitmap = await page.Document!.RenderPageAsync(page.Reference.Page, w, h, PdfRenderOptions.None, PdfWorkPriority.Background, ct);
                int turns = page.QuarterTurns;
                found[page] = await Task.Run(() =>
                {
                    var turned = PageBitmaps.Turn(bitmap, turns);
                    return Core.Imaging.PageImageAnalysis.SkewDegrees(turned.Pixels, turned.Width, turned.Height, turned.Stride);
                }, ct);
                BusyProgress.Value = (i + 1.0) / targets.Count;
            }

            return found;
        });
        if (angles == null)
        {
            return;
        }

        var crooked = angles.Where(a => a.Value != 0 && a.Value != (a.Key.Geometry?.StraightenDegrees ?? 0)).ToList();
        if (crooked.Count == 0)
        {
            Toaster.Show(targets.Count == 1 ? "This page already looks straight" : "These pages already look straight", "\uED5E");
            return;
        }

        RememberForUndo();
        foreach (var (page, angle) in crooked)
        {
            page.Geometry = Normalize((page.Geometry ?? new PageGeometry()) with { StraightenDegrees = (float)angle });
        }

        AfterChange();
        Toaster.Show(crooked.Count == 1 ? $"Straightened by {Math.Abs(crooked[0].Value):0.0}°" : $"Straightened {crooked.Count:N0} pages", "\uED5E", "Undo", Undo);
    }

    /// <summary>A scanned book spread becomes two pages: the left half, then the right half.</summary>
    private void SplitDoublePages()
    {
        var selected = SelectedInOrder();
        if (selected.Count == 0)
        {
            Toaster.Show("Select the double pages first", "\uE8C6");
            return;
        }

        RememberForUndo();
        foreach (var page in Enumerable.Reverse(selected))
        {
            var crop = page.Geometry?.Crop ?? PageGeometry.Whole;
            var left = crop with { Width = crop.Width / 2 };
            var right = crop with { X = crop.X + (crop.Width / 2), Width = crop.Width / 2 };
            var copy = NewPage(page.Reference, page.Document, page.FromFile, page.Size);
            copy.QuarterTurns = page.QuarterTurns;
            copy.Geometry = (page.Geometry ?? new PageGeometry()) with { Crop = right };
            page.Geometry = (page.Geometry ?? new PageGeometry()) with { Crop = left };
            _pages.Insert(_pages.IndexOf(page) + 1, copy);
        }

        AfterChange();
        Toaster.Show(selected.Count == 1 ? "Split into two pages" : $"Split {selected.Count:N0} spreads into {selected.Count * 2:N0} pages", "\uE8C6", "Undo", Undo);
    }

    private void ResetShape()
    {
        var targets = SelectedOrAll().Where(p => p.Geometry != null).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        RememberForUndo();
        foreach (var page in targets)
        {
            page.Geometry = null;
        }

        AfterChange();
    }

    /// <summary>2 or 4 pages on each sheet, saved as a new PDF for printing.</summary>
    private async Task PagesPerSheetAsync()
    {
        if (HasChanges)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Save first", "Pages per sheet works on the file as it's saved. Save your page changes first.");
            return;
        }

        var perSheet = new PillBar();
        perSheet.AddItem("2 per sheet", "\uE8A9", "2");
        perSheet.AddItem("4 per sheet", "\uE80A", "4");
        perSheet.SelectedIndex = 0;
        var paper = new ComboBox { Header = "Paper", MinWidth = 200 };
        paper.Items.Add("A4");
        paper.Items.Add("US Letter");
        paper.SelectedIndex = System.Globalization.RegionInfo.CurrentRegion.IsMetric ? 0 : 1;
        var note = new TextBlock
        {
            Text = "Saved as a new PDF for printing. Links and comments aren't carried over.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
        };
        var panel = new StackPanel { Spacing = 16, Width = 360 };
        panel.Children.Add(perSheet);
        panel.Children.Add(paper);
        panel.Children.Add(note);
        var dialog = new ContentDialog { Title = "Pages per sheet", Content = panel, PrimaryButtonText = "Save as...", CloseButtonText = "Cancel" };
        if (await Dialogs.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary)
        {
            return;
        }

        bool four = perSheet.SelectedIndex == 1;
        var size = paper.SelectedIndex == 0 ? PaperSizes.A4 : PaperSizes.Letter;
        var sheet = four ? size : new PdfPageSize(size.Height, size.Width); // 2-up sits side by side on landscape paper
        string name = $"{Path.GetFileNameWithoutExtension(_document.FilePath)} ({(four ? 4 : 2)} per sheet).pdf";
        string? path = await Pickers.PickSavePdfAsync(GetWindowHandle(), name, _document.FilePath);
        if (path == null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Pick a different name", "Pages per sheet makes a new file, so the open file stays as it is.");
            return;
        }

        var input = new PdfInputFile(_document.FilePath, _document.Password);
        var result = await RunAsync("Arranging pages", ct => PdfEngine.Shared.NUpAsync(input, path, sheet, 2, four ? 2 : 1, ct), path);
        if (result != null)
        {
            Toaster.Show($"Saved {result.PageCount:N0} sheets: {Path.GetFileName(path)}", "\uE8A9", "Open", () => OpenRequested?.Invoke(this, path));
        }
    }

    private static PageGeometry? Normalize(PageGeometry geometry) => geometry.IsEmpty ? null : geometry;

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

    private void OnInsertBlankSized(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((FrameworkElement)sender).Tag).Split(',');
        InsertBlank(new PdfPageSize(
            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void OnMoreMenuOpening(object sender, object e)
    {
        bool any = Board.SelectedItems.Count > 0 && _busy == null;
        Rotate180Item.IsEnabled = ReplaceItem.IsEnabled = SplitDoubleItem.IsEnabled = any;
        ResetShapeItem.IsEnabled = SelectedOrAll().Any(p => p.Geometry != null);
        MoveStartItem.IsEnabled = MoveEndItem.IsEnabled = any && Board.SelectedItems.Count < _pages.Count;
        ReverseItem.Text = Board.SelectedItems.Count > 1 ? "Reverse the selected pages" : "Reverse all pages";
    }

    private void OnRotate180(object sender, RoutedEventArgs e) => Rotate(SelectedInOrder(), 2);

    private void OnCrop(object sender, RoutedEventArgs e) => Guard.Run(OpenCropAsync, "Opening crop");

    private void OnPageSize(object sender, RoutedEventArgs e) => Guard.Run(PageSizeAsync, "Changing page size");

    private void OnStraighten(object sender, RoutedEventArgs e) => Guard.Run(StraightenAsync, "Straightening");

    private void OnSplitDouble(object sender, RoutedEventArgs e) => SplitDoublePages();

    private void OnResetShape(object sender, RoutedEventArgs e) => ResetShape();

    private void OnPagesPerSheet(object sender, RoutedEventArgs e) => Guard.Run(PagesPerSheetAsync, "Pages per sheet");

    private void OnMoveToStart(object sender, RoutedEventArgs e) => MoveSelected(toStart: true);

    private void OnMoveToEnd(object sender, RoutedEventArgs e) => MoveSelected(toStart: false);

    private void OnReverse(object sender, RoutedEventArgs e) => Reverse();

    private void OnReplace(object sender, RoutedEventArgs e) => Guard.Run(ReplaceAsync, "Replacing pages");

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
