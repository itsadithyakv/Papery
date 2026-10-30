using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Papery.Commands;
using Papery.Controls;
using Papery.Core.Storage;
using Papery.Core.Viewing;
using Papery.Pdf;
using Papery.Services;
using Papery.Services.Printing;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Papery.Views;

/// <summary>One open PDF: toolbars, sidebar (pages, bookmarks, search) and the page viewer.</summary>
public sealed partial class DocumentView : UserControl
{
    private const int MaxSearchResults = 5000;

    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];
    private readonly ObservableCollection<SearchResultItem> _results = [];
    private readonly BitmapCache<(int Page, ReadingMode Mode)> _thumbnailCache = new(48L * 1024 * 1024);
    private readonly DispatcherQueueTimer _saveTimer;

    private PdfDocument? _document;
    private bool _syncingThumbnails;
    private CancellationTokenSource? _searchCts;
    private string _searchedQuery = string.Empty;
    private PdfSearchOptions _searchedOptions;
    private int _currentResult = -1;
    private CancellationTokenSource? _printCts;
    private (ViewMode Mode, FitMode Fit, double Zoom)? _beforePresenting;

    public DocumentView()
    {
        InitializeComponent();
        ThumbnailList.ItemsSource = _thumbnails;
        BuildSwitchers();
        BuildAnnotateToolbar();
        BuildFillToolbar();
        BuildEditToolbar();
        SearchResults.ItemsSource = _results;

        Viewer.CurrentPageChanged += (_, _) => OnCurrentPageChanged();
        Viewer.ZoomChanged += (_, _) => { UpdateZoomText(); SaveStateSoon(); };
        Viewer.LinkClicked += (_, link) => _ = OnLinkClickedAsync(link);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveState();

        // Ctrl + the main-keyboard +/- keys (XAML can't name these OEM keys).
        AddAccelerator((VirtualKey)187, VirtualKeyModifiers.Control, (_, a) => { Viewer.ZoomIn(); a.Handled = true; });
        AddAccelerator((VirtualKey)189, VirtualKeyModifiers.Control, (_, a) => { Viewer.ZoomOut(); a.Handled = true; });

        // While presenting, these keys always change slides (whatever has focus). Otherwise they're left alone.
        (VirtualKey Key, bool Forward)[] slideKeys =
        [
            (VirtualKey.Right, true), (VirtualKey.Down, true), (VirtualKey.Space, true), (VirtualKey.PageDown, true),
            (VirtualKey.Left, false), (VirtualKey.Up, false), (VirtualKey.PageUp, false),
        ];
        foreach (var (key, forward) in slideKeys)
        {
            AddAccelerator(key, VirtualKeyModifiers.None, (_, a) =>
            {
                if (!IsPresenting)
                {
                    return;
                }

                if (forward)
                {
                    Viewer.NextPage();
                }
                else
                {
                    Viewer.PreviousPage();
                }

                a.Handled = true;
            });
        }
    }

    /// <summary>Asks the window to enter (true) or leave (false) full screen for presenting.</summary>
    public event EventHandler<bool>? PresentationRequested;

    /// <summary>Asks the window to show the Settings or Help tab ("settings" / "help").</summary>
    public event EventHandler<string>? NavigationRequested;

    public string FilePath { get; private set; } = string.Empty;

    public string FileName => Path.GetFileName(FilePath);

    public PdfDocument? Document => _document;

    /// <summary>Set by the window, so dialogs owned by Win32 (printer properties) have a parent.</summary>
    internal Func<nint>? GetWindowHandle { get; set; }

    public bool IsPresenting => _beforePresenting != null;

    public void Load(PdfDocument document)
    {
        _document = document;
        FilePath = document.FilePath;
        var settings = AppServices.Settings.Current;
        var state = settings.RememberPosition ? AppServices.ViewState.Get(FilePath) : null;

        Viewer.SetDocument(document, state?.Page ?? 0, state?.Zoom ?? 0, state?.ViewMode ?? settings.DefaultViewMode, settings.ReadingMode);
        PageCountText.Text = $"of {document.PageCount:N0}";
        UpdatePageBox();
        UpdateZoomText();
        UpdateLayoutChecks();
        UpdateReadingChecks();
        SetSidebarOpen(settings.SidebarOpen);

        for (int i = 0; i < document.PageCount; i++)
        {
            _thumbnails.Add(new ThumbnailItem(i, document.PageSizes[i]));
        }

        _ = LoadOutlineAsync();
        OnCurrentPageChanged(); // highlight the opening page's thumbnail

        if (document.WasRepaired)
        {
            ShowNotice("This file was damaged", "Papery repaired it so you can read it. Your original file has not been changed.", InfoBarSeverity.Warning);
        }

        AppServices.ViewState.AddRecent(FilePath);
        SaveStateSoon();
        if (IsLoaded)
        {
            Viewer.FocusPages();
        }
        else
        {
            Loaded += FocusOnce;
        }

        // A one-time, inline (not pop-up) offer of the tour for first-time users.
        if (!document.WasRepaired && !settings.ViewerTourOffered)
        {
            AppServices.Settings.Update(s => s.ViewerTourOffered = true);
            ShowNotice("New to Papery?", "A one-minute tour shows where everything is.");
            var tour = new Button { Content = "Take the tour" };
            tour.Click += (_, _) =>
            {
                Notice.IsOpen = false;
                StartTour();
            };
            Notice.ActionButton = tour;
        }

        void FocusOnce(object sender, RoutedEventArgs e)
        {
            Loaded -= FocusOnce;
            Viewer.FocusPages();
        }
    }

    /// <summary>Walks through the viewer's controls with pointer tips. Only ever started by the user.</summary>
    public void StartTour()
    {
        if (IsPresenting)
        {
            return;
        }

        var steps = new List<GuidedTour.Step>
        {
            new(() => Modes, "Modes", "Each mode shows only its own tools: View for reading; Organise, Annotate, Edit and the rest for changing the document."),
            new(() => SidebarToggle, "Sidebar", "Show or hide page thumbnails, bookmarks and search results."),
            new(() => SidebarTabs, "Pages, bookmarks and search", "Click a thumbnail or bookmark to jump there. Search lists every match with a preview."),
            new(() => PageBox, "Go to a page", "Type a page number and press Enter (Ctrl+G). Home and End jump to the first and last page."),
            new(() => ZoomButton, "Zoom", "Pick a zoom, or fit the width or whole page. Ctrl + mouse wheel or a pinch zooms smoothly."),
            new(() => LayoutButton, "Page layout", "One page at a time, continuous scrolling, two pages side by side, or a book with the cover on its own."),
            new(() => ReadingButton, "Page colours", "Dark and Sepia recolour pages for comfortable reading. Photos keep their real colours."),
            new(() => SearchButton, "Search", "Find any word in the document (Ctrl+F). F3 goes to the next match."),
            new(() => PrintButton, "Print", "Choose pages, several pages per sheet, booklets and scaling (Ctrl+P)."),
            new(() => MoreButton, "More", "Present full screen, document properties, this tour and keyboard shortcuts. Tip: Ctrl+K lets you type any command."),
        };
        new GuidedTour((Panel)Content, steps).Start();
    }

    /// <summary>Saves where the user was, stops background work and closes the file.</summary>
    public async Task CloseAsync()
    {
        _closingTab = true;
        await ExitOrganiseAsync(saved: false);
        _saveTimer.Stop();
        SaveState();
        _searchCts?.Cancel();
        _printCts?.Cancel();
        var document = _document;
        _document = null;
        Viewer.SetDocument(null);
        _thumbnails.Clear();
        _thumbnailCache.Clear();
        if (document != null)
        {
            await document.DisposeAsync();
        }

        DeleteUndoCopies();
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Notice.ActionButton = null;
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsClosable = true;
        Notice.IsOpen = true;
    }

    // ================================================================= commands (Ctrl+K)

    internal IReadOnlyList<AppCommand> GetCommands()
    {
        bool hasDoc() => _document != null;
        return
        [
            new("Print", "", () => _ = PrintAsync(), "Ctrl+P", "printer paper", hasDoc),
            new("Search in document", "", ShowSearch, "Ctrl+F", "find text look", hasDoc),
            new("Go to page", "", FocusPageBox, "Ctrl+G", "jump number", hasDoc),
            new("First page", "", () => Viewer.GoToPage(0), "Home", "start beginning", hasDoc),
            new("Last page", "", () => Viewer.GoToPage(int.MaxValue), "End", "end", hasDoc),
            new("Zoom in", "", Viewer.ZoomIn, "Ctrl++", "bigger magnify", hasDoc),
            new("Zoom out", "", Viewer.ZoomOut, "Ctrl+-", "smaller", hasDoc),
            new("Fit width", "", () => Viewer.SetFitMode(FitMode.Width), "Ctrl+2", "zoom", hasDoc),
            new("Fit page", "", () => Viewer.SetFitMode(FitMode.Page), "Ctrl+0", "zoom whole", hasDoc),
            new("Actual size", "", () => Viewer.SetZoom(1), "Ctrl+1", "zoom 100%", hasDoc),
            new("Single page view", "", () => SetViewMode(ViewMode.SinglePage), Keywords: "layout one", CanExecute: hasDoc),
            new("Continuous scrolling", "", () => SetViewMode(ViewMode.Continuous), Keywords: "layout scroll", CanExecute: hasDoc),
            new("Two pages side by side", "", () => SetViewMode(ViewMode.TwoPage), Keywords: "layout spread facing", CanExecute: hasDoc),
            new("Book view", "", () => SetViewMode(ViewMode.Book), Keywords: "layout cover spread", CanExecute: hasDoc),
            new("Reading mode: Light", "", () => SetReadingMode(ReadingMode.Light), Keywords: "colour normal", CanExecute: hasDoc),
            new("Reading mode: Dark", "", () => SetReadingMode(ReadingMode.Dark), Keywords: "night colour invert", CanExecute: hasDoc),
            new("Reading mode: Sepia", "", () => SetReadingMode(ReadingMode.Sepia), Keywords: "warm colour", CanExecute: hasDoc),
            new("Present full screen", "", EnterPresentation, "F5", "slideshow fullscreen presentation", hasDoc),
            new("Show or hide sidebar", "", () => SetSidebarOpen(Sidebar.Visibility != Visibility.Visible), Keywords: "thumbnails pane panel", CanExecute: hasDoc),
            new("Bookmarks", "", () => { SetSidebarOpen(true); SidebarTabs.SelectedIndex = BookmarksTabIndex; }, Keywords: "outline chapters contents", CanExecute: hasDoc),
            new("Copy selected text", "", CopySelection, "Ctrl+C", "clipboard", () => Viewer.Selection != null),
            new("Document properties", "", () => _ = ShowPropertiesAsync(), "Ctrl+D", "info details metadata author", hasDoc),
            new("Show in folder", "", ShowInFolder, Keywords: "explorer location", CanExecute: hasDoc),
            new("Take the tour", "", StartTour, Keywords: "help tutorial guide learn", CanExecute: hasDoc),
            .. PageToolCommands(),
        ];
    }

    // ================================================================= page and zoom toolbar

    private void OnCurrentPageChanged()
    {
        UpdatePageBox();
        SaveStateSoon();
        if (Sidebar.Visibility == Visibility.Visible && ThumbnailList.Visibility == Visibility.Visible)
        {
            _syncingThumbnails = true;
            ThumbnailList.SelectedIndex = Viewer.CurrentPage;
            ThumbnailList.ScrollIntoView(ThumbnailList.SelectedItem);
            _syncingThumbnails = false;
        }
    }

    private void UpdatePageBox()
    {
        if (PageBox.FocusState == FocusState.Unfocused)
        {
            PageBox.Text = (Viewer.CurrentPage + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    private void UpdateZoomText() => ZoomButton.Content = Zoom.Label(Viewer.ZoomFactor);

    private void FocusPageBox()
    {
        PageBox.Focus(FocusState.Keyboard);
        PageBox.SelectAll();
    }

    private void GoToTypedPage()
    {
        if (int.TryParse(PageBox.Text.Trim(), out int page) && _document != null)
        {
            Viewer.GoToPage(Math.Clamp(page, 1, _document.PageCount) - 1);
        }

        UpdatePageBox();
    }

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            GoToTypedPage();
            Viewer.FocusPages();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Viewer.FocusPages();
            UpdatePageBox();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) => UpdatePageBox();

    private void OnPageBoxGotFocus(object sender, RoutedEventArgs e) => PageBox.SelectAll();

    private void OnPreviousPage(object sender, RoutedEventArgs e) => Viewer.PreviousPage();

    private void OnNextPage(object sender, RoutedEventArgs e) => Viewer.NextPage();

    private void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();

    private void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();

    private void OnFitWidth(object sender, RoutedEventArgs e) => Viewer.SetFitMode(FitMode.Width);

    private void OnFitPage(object sender, RoutedEventArgs e) => Viewer.SetFitMode(FitMode.Page);

    private void OnActualSize(object sender, RoutedEventArgs e) => Viewer.SetZoom(1);

    private void OnZoomPreset(object sender, RoutedEventArgs e) =>
        Viewer.SetZoom(double.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));

    private void OnLayoutClick(object sender, RoutedEventArgs e) =>
        SetViewMode(Enum.Parse<ViewMode>((string)((FrameworkElement)sender).Tag));

    private void OnReadingClick(object sender, RoutedEventArgs e) =>
        SetReadingMode(Enum.Parse<ReadingMode>((string)((FrameworkElement)sender).Tag));

    private void SetViewMode(ViewMode mode)
    {
        Viewer.SetViewMode(mode);
        UpdateLayoutChecks();
        SaveStateSoon();
    }

    private void SetReadingMode(ReadingMode mode)
    {
        Viewer.SetReadingMode(mode);
        AppServices.Settings.Update(s => s.ReadingMode = mode); // a preference for every document
        UpdateReadingChecks();

        // Recolour the thumbnails that are on screen; the rest update as they scroll into view.
        foreach (var item in _thumbnails)
        {
            item.Loading?.Cancel();
            if (ThumbnailList.ContainerFromItem(item) != null)
            {
                _ = LoadThumbnailAsync(item);
            }
            else
            {
                item.Image = null;
            }
        }
    }

    private void UpdateLayoutChecks()
    {
        LayoutSingle.IsChecked = Viewer.ViewMode == ViewMode.SinglePage;
        LayoutContinuous.IsChecked = Viewer.ViewMode == ViewMode.Continuous;
        LayoutTwoPage.IsChecked = Viewer.ViewMode == ViewMode.TwoPage;
        LayoutBook.IsChecked = Viewer.ViewMode == ViewMode.Book;
        LayoutIcon.Glyph = Viewer.ViewMode is ViewMode.TwoPage or ViewMode.Book ? "" : "";
    }

    private void UpdateReadingChecks()
    {
        ReadingLight.IsChecked = Viewer.ReadingMode == ReadingMode.Light;
        ReadingDark.IsChecked = Viewer.ReadingMode == ReadingMode.Dark;
        ReadingSepia.IsChecked = Viewer.ReadingMode == ReadingMode.Sepia;
    }

    // Sidebar tabs, in PillBar order.
    private const int PagesTabIndex = 0;
    private const int BookmarksTabIndex = 1;
    private const int SearchTabIndex = 2;
    private const int CommentsTabIndex = 3;

    /// <summary>Fills the mode bar and sidebar tabs (PillBars) and hooks up their selection.</summary>
    private void BuildSwitchers()
    {
        (string Name, string Glyph)[] modes =
        [
            ("View", ""), ("Organise", ""), ("Annotate", ""), ("Edit", ""),
            ("Fill & Sign", ""), ("Convert", ""), ("Protect", ""), ("Tools", ""),
        ];
        foreach (var (name, glyph) in modes)
        {
            Modes.AddItem(name, glyph, name, name.Replace("&", "and", StringComparison.Ordinal));
        }

        Modes.SelectionChanged += (_, index) => OnModeSelected(index, modes[index].Name);

        SidebarTabs.AddIconItem("\uE8A9", "Pages");
        SidebarTabs.AddIconItem("\uE8A4", "Bookmarks");
        SidebarTabs.AddIconItem("\uE721", "Search");
        SidebarTabs.AddIconItem("\uE90A", "Comments");
        SidebarTabs.SelectionChanged += (_, _) => OnSidebarTabChanged();
    }

    // ================================================================= sidebar

    private void OnSidebarToggle(object sender, RoutedEventArgs e) => SetSidebarOpen(SidebarToggle.IsChecked == true);

    private void SetSidebarOpen(bool open)
    {
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggle.IsChecked = open;
        if (AppServices.Settings.Current.SidebarOpen != open)
        {
            AppServices.Settings.Update(s => s.SidebarOpen = open);
        }
    }

    private void OnSidebarTabChanged()
    {
        int tab = SidebarTabs.SelectedIndex;
        ThumbnailList.Visibility = tab == PagesTabIndex ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = tab == BookmarksTabIndex ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = tab == SearchTabIndex ? Visibility.Visible : Visibility.Collapsed;
        CommentsPanel.Visibility = tab == CommentsTabIndex ? Visibility.Visible : Visibility.Collapsed;
        if (tab == CommentsTabIndex)
        {
            Guard.Run(LoadCommentsAsync, "Reading comments");
        }
        if (tab == PagesTabIndex && _document != null)
        {
            OnCurrentPageChanged();
        }
    }

    private void OnThumbnailContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ThumbnailItem item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            item.Loading?.Cancel();
            item.Image = null; // let the cache, not the list, decide what stays in memory
            return;
        }

        if (item.Image == null)
        {
            _ = LoadThumbnailAsync(item);
        }
    }

    private async Task LoadThumbnailAsync(ThumbnailItem item)
    {
        var document = _document;
        if (document == null)
        {
            return;
        }

        // Thumbnails use the same page colours as the pages (Dark/Sepia), so the sidebar matches.
        var mode = Viewer.ReadingMode;
        if (_thumbnailCache.TryGet((item.PageIndex, mode), out var cached))
        {
            item.Image = cached;
            return;
        }

        item.Loading?.Cancel();
        var cts = new CancellationTokenSource();
        item.Loading = cts;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        var size = document.PageSizes[item.PageIndex];
        int w = Math.Max(16, (int)(item.Width * scale));
        int h = Math.Min(Math.Max(16, (int)(w * size.Height / size.Width)), w * 4);
        try
        {
            var bitmap = await document.RenderPageAsync(item.PageIndex, w, h, PdfRenderOptions.Annotations, PdfWorkPriority.Nearby, cts.Token);
            if (mode != ReadingMode.Light)
            {
                var areas = await document.GetImageAreasAsync(item.PageIndex, cts.Token);
                await Task.Run(() => PageBitmaps.ApplyReadingMode(bitmap, mode, areas, w, h), cts.Token);
            }

            WriteableBitmap image = PageBitmaps.ToWriteableBitmap(bitmap);
            _thumbnailCache.Add((item.PageIndex, mode), image);
            if (!cts.IsCancellationRequested && document == _document)
            {
                item.Image = image;
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
            Log.Error($"Thumbnail for page {item.PageIndex + 1} failed", ex);
        }
    }

    private void OnThumbnailSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingThumbnails && ThumbnailList.SelectedItem is ThumbnailItem item && item.PageIndex != Viewer.CurrentPage)
        {
            Viewer.GoToPage(item.PageIndex);
        }
    }

    private async Task LoadOutlineAsync()
    {
        try
        {
            var outline = await _document!.GetOutlineAsync();
            var nodes = outline.Select(o => new OutlineNode(o, 0)).ToList();
            OutlineTree.ItemsSource = nodes;
            NoBookmarksText.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading bookmarks failed", ex);
            NoBookmarksText.Visibility = Visibility.Visible;
        }
    }

    private void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineNode { PageIndex: int page })
        {
            Viewer.GoToPage(page);
        }
    }

    // ================================================================= search

    private void ShowSearch()
    {
        SetSidebarOpen(true);
        SidebarTabs.SelectedIndex = SearchTabIndex;
        if (Viewer.Selection is { Text.Length: > 0 and < 80 } selection)
        {
            SearchBox.Text = selection.Text.Trim(); // search for the selected words, like other apps
        }

        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => ShowSearch();

    private PdfSearchOptions CurrentSearchOptions =>
        (MatchCaseBox.IsChecked == true ? PdfSearchOptions.MatchCase : 0) | (WholeWordBox.IsChecked == true ? PdfSearchOptions.WholeWord : 0);

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool back = InputKeyboardSourceHelper.IsShiftDown();
            if (SearchBox.Text.Trim() != _searchedQuery || CurrentSearchOptions != _searchedOptions)
            {
                _ = RunSearchAsync(SearchBox.Text.Trim());
            }
            else
            {
                MoveResult(back ? -1 : 1);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Viewer.FocusPages();
            e.Handled = true;
        }
    }

    private void OnSearchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_searchedQuery.Length > 0)
        {
            _ = RunSearchAsync(SearchBox.Text.Trim());
        }
    }

    /// <summary>Searches page by page in the background, showing results as they're found.</summary>
    private async Task RunSearchAsync(string query)
    {
        _searchCts?.Cancel();
        _results.Clear();
        _currentResult = -1;
        _searchedQuery = query;
        _searchedOptions = CurrentSearchOptions;
        Viewer.SetSearchResults([], null);
        var document = _document;
        if (query.Length == 0 || document == null)
        {
            SearchStatus.Text = string.Empty;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var options = _searchedOptions;
        var all = new List<PdfSearchHit>();
        int startPage = Viewer.CurrentPage;
        try
        {
            for (int page = 0; page < document.PageCount && all.Count < MaxSearchResults; page++)
            {
                var hits = await document.FindOnPageAsync(page, query, options, cts.Token);
                if (hits.Count > 0)
                {
                    all.AddRange(hits);
                    foreach (var hit in hits)
                    {
                        _results.Add(new SearchResultItem(hit));
                    }

                    // Jump to the first match at or after the page the user is reading.
                    if (_currentResult < 0 && page >= startPage)
                    {
                        SelectResult(_results.Count - hits.Count);
                    }

                    if (all.Count == hits.Count || page % 20 == 0)
                    {
                        Viewer.SetSearchResults(all, CurrentHit);
                    }
                }

                if (page % 25 == 0)
                {
                    SearchStatus.Text = $"Searching… page {page + 1:N0} of {document.PageCount:N0} · {all.Count:N0} found";
                }
            }

            Viewer.SetSearchResults(all, CurrentHit);
            if (_currentResult < 0 && _results.Count > 0)
            {
                SelectResult(0);
            }

            SearchStatus.Text = all.Count switch
            {
                0 => $"No results for “{query}”.",
                >= MaxSearchResults => $"Showing the first {MaxSearchResults:N0} results.",
                _ => ResultPosition(),
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private PdfSearchHit? CurrentHit => _currentResult >= 0 && _currentResult < _results.Count ? _results[_currentResult].Hit : null;

    private string ResultPosition() => _results.Count == 1 ? "1 result" : $"{_currentResult + 1:N0} of {_results.Count:N0} results";

    private void SelectResult(int index)
    {
        if (_results.Count == 0)
        {
            return;
        }

        _currentResult = ((index % _results.Count) + _results.Count) % _results.Count;
        SearchResults.SelectedIndex = _currentResult;
        SearchResults.ScrollIntoView(SearchResults.SelectedItem);
        Viewer.ShowSearchHit(_results[_currentResult].Hit);
        if (_searchCts?.IsCancellationRequested != false || !SearchStatus.Text.StartsWith("Searching", StringComparison.Ordinal))
        {
            SearchStatus.Text = ResultPosition();
        }
    }

    private void MoveResult(int delta)
    {
        if (_results.Count == 0)
        {
            if (SearchBox.Text.Trim().Length > 0)
            {
                _ = RunSearchAsync(SearchBox.Text.Trim());
            }

            return;
        }

        SelectResult(_currentResult + delta);
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => MoveResult(1);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => MoveResult(-1);

    private void OnSearchResultClick(object sender, ItemClickEventArgs e) =>
        SelectResult(_results.IndexOf((SearchResultItem)e.ClickedItem));

    // ================================================================= links, copy, print, properties

    private async Task OnLinkClickedAsync(PdfLink link)
    {
        if (link.TargetPage is int page)
        {
            Viewer.GoToPage(page, link.TargetY);
            return;
        }

        if (link.Uri is not string uri)
        {
            return;
        }

        // Only web and email links: a PDF must never be able to launch programs or open local files.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https" or "mailto"))
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Link not opened", $"For your safety, Papery only opens web and email links. This link goes to:\n\n{uri}");
            return;
        }

        if (await Dialogs.ConfirmAsync(XamlRoot, "Open this link?", $"{target.OriginalString}\n\nIt will open in your web browser or email app.", "Open link"))
        {
            await Launcher.LaunchUriAsync(target);
        }
    }

    private void CopySelection()
    {
        if (Viewer.Selection is not { } selection || _document == null)
        {
            return;
        }

        if (!_document.CanCopy)
        {
            ShowNotice("Copying isn't allowed", "The author of this PDF has turned off copying text.");
            return;
        }

        var package = new DataPackage();
        package.SetText(selection.Text);
        Clipboard.SetContent(package);
    }

    /// <summary>
    /// Printers can't draw form fields separately, so a form prints from a temporary copy with its
    /// answers burned in (still sharp vectors), which is deleted straight afterwards.
    /// </summary>
    private async Task<int> PrintFormAwareAsync(PdfDocument document, PrintJob job, IProgress<(int Done, int Total)> progress, CancellationToken cancellationToken)
    {
        if (document.FormType == PdfFormType.None)
        {
            return await PrintService.PrintAsync(document, FileName, job, progress, cancellationToken);
        }

        string copy = await document.CreatePrintCopyAsync(cancellationToken);
        try
        {
            await using var printable = await PdfEngine.Shared.OpenAsync(copy, document.Password, cancellationToken);
            return await PrintService.PrintAsync(printable, FileName, job, progress, cancellationToken);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private async Task PrintAsync()
    {
        if (_document == null)
        {
            return;
        }

        if (!_document.CanPrint)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "Printing isn't allowed", "The author of this PDF has turned off printing.");
            return;
        }

        var dialog = new PrintDialog(_document, Viewer.CurrentPage, GetWindowHandle?.Invoke() ?? 0);
        if (await Dialogs.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary || dialog.Job is not { } job)
        {
            return;
        }

        _printCts?.Cancel();
        var cts = new CancellationTokenSource();
        _printCts = cts;
        ShowNotice("Printing…", "Preparing pages", InfoBarSeverity.Informational);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => cts.Cancel();
        Notice.ActionButton = cancel;
        Notice.IsClosable = false;

        var progress = new Progress<(int Done, int Total)>(p => Notice.Message = $"Sending sheet {p.Done} of {p.Total}");
        try
        {
            int sides = await PrintFormAwareAsync(_document, job, progress, cts.Token);
            Notice.IsOpen = false;
            Toaster.Show($"Sent to the printer · {sides} side{(sides == 1 ? string.Empty : "s")} of paper", "");
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Printing cancelled", "Nothing more will be sent to the printer.");
        }
        catch (Exception ex)
        {
            Log.Error("Printing failed", ex);
            ShowNotice("Papery couldn't print", "Check the printer is switched on and connected, then try again.", InfoBarSeverity.Error);
        }
    }

    private async Task ShowPropertiesAsync()
    {
        if (_document != null)
        {
            await PropertiesDialog.ShowAsync(XamlRoot, _document);
        }
    }

    private void ShowInFolder() => Shell.ShowInFolder(FilePath);

    private void OnPrintClick(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private void OnPropertiesClick(object sender, RoutedEventArgs e) => _ = ShowPropertiesAsync();

    private void OnShowInFolderClick(object sender, RoutedEventArgs e) => ShowInFolder();

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => _ = Viewer.SelectAllOnCurrentPageAsync();

    private void OnViewerMenuOpening(object sender, object e) => CopyMenuItem.IsEnabled = Viewer.Selection != null;

    // ================================================================= presentation

    public void EnterPresentation()
    {
        if (_document == null || IsPresenting)
        {
            return;
        }

        _beforePresenting = (Viewer.ViewMode, Viewer.FitMode, Viewer.ZoomFactor);
        ModeBar.Visibility = ToolbarArea.Visibility = Sidebar.Visibility = Visibility.Collapsed;
        Notice.IsOpen = false;
        PresentationRequested?.Invoke(this, true);
        Viewer.SetPresenting(true);
        Viewer.SetViewMode(ViewMode.SinglePage);
        Viewer.SetFitMode(FitMode.Page);
        Viewer.FocusPages();
    }

    public void ExitPresentation()
    {
        if (_beforePresenting is not { } before)
        {
            return;
        }

        _beforePresenting = null;
        int presentedPage = Viewer.CurrentPage; // stay on the slide the user stopped at
        PresentationRequested?.Invoke(this, false); // restore the window first, so fitting uses its real size
        ModeBar.Visibility = ToolbarArea.Visibility = Visibility.Visible;
        SetSidebarOpen(AppServices.Settings.Current.SidebarOpen);
        Viewer.SetPresenting(false);
        Viewer.SetViewMode(before.Mode);
        if (before.Fit == FitMode.None)
        {
            Viewer.SetZoom(before.Zoom);
        }
        else
        {
            Viewer.SetFitMode(before.Fit);
        }

        Viewer.GoToPage(presentedPage);
        Viewer.FocusPages();
    }

    private void OnPresentClick(object sender, RoutedEventArgs e) => EnterPresentation();

    private void OnTourClick(object sender, RoutedEventArgs e) => StartTour();

    private void OnHelpClick(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke(this, "help");

    private void OnSettingsClick(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke(this, "settings");

    // ================================================================= keyboard shortcuts

    private static bool TextBoxHasFocus(UIElement anyElement) =>
        FocusManager.GetFocusedElement(anyElement.XamlRoot) is TextBox or PasswordBox or AutoSuggestBox;

    private void OnFindShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { ShowSearch(); a.Handled = true; }

    private void OnFindNextShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { MoveResult(1); a.Handled = true; }

    private void OnFindPreviousShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { MoveResult(-1); a.Handled = true; }

    private void OnPrintShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { _ = PrintAsync(); a.Handled = true; }

    private void OnPropertiesShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { _ = ShowPropertiesAsync(); a.Handled = true; }

    private void OnGoToPageShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { FocusPageBox(); a.Handled = true; }

    private void OnFitPageShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Viewer.SetFitMode(FitMode.Page); a.Handled = true; }

    private void OnActualSizeShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Viewer.SetZoom(1); a.Handled = true; }

    private void OnFitWidthShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Viewer.SetFitMode(FitMode.Width); a.Handled = true; }

    private void OnZoomInShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Viewer.ZoomIn(); a.Handled = true; }

    private void OnZoomOutShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { Viewer.ZoomOut(); a.Handled = true; }

    private void OnPresentShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { EnterPresentation(); a.Handled = true; }

    private void OnCopyShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus(this) && Viewer.Selection != null)
        {
            CopySelection();
            a.Handled = true;
        }
    }

    private void OnSelectAllShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (!TextBoxHasFocus(this))
        {
            _ = Viewer.SelectAllOnCurrentPageAsync();
            a.Handled = true;
        }
    }

    private void OnEscapeShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (IsPresenting)
        {
            ExitPresentation();
            a.Handled = true;
        }
        else if (Viewer.Selection != null)
        {
            Viewer.ClearSelection();
            a.Handled = true;
        }
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += handler;
        KeyboardAccelerators.Add(accelerator);
    }

    // ================================================================= remembering position

    private void SaveStateSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        if (_document == null || !AppServices.Settings.Current.RememberPosition || IsPresenting)
        {
            return;
        }

        AppServices.ViewState.Set(FilePath, Viewer.CurrentPage, Viewer.SavedZoom, Viewer.ViewMode);
        _ = AppServices.ViewState.SaveAsync();
    }
}

/// <summary>Reads modifier keys without needing a key event.</summary>
internal static class InputKeyboardSourceHelper
{
    public static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
