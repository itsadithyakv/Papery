using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Commands;
using Papery.Core.Storage;
using Papery.Services;
using Papery.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Papery;

/// <summary>
/// A Papery window: tabs in the title bar (like Notepad and File Explorer), each tab holding
/// the home screen, a document or settings. Tabs can be dragged out into new windows.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _closeApproved;

    public MainWindow(bool withHomeTab = true)
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Papery.ico"));
        ApplyBackdrop();
        ApplyTheme();
        RestoreSize();

        AppWindow.Closing += OnClosing;
        Activated += (_, e) => _active = e.WindowActivationState != WindowActivationState.Deactivated;
        Guard.Failed += OnGuardFailed;
        Closed += (_, _) => Guard.Failed -= OnGuardFailed;
        AppServices.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) => AppServices.Settings.Changed -= OnSettingsChanged;
        Root.Loaded += (_, _) => UpdateCaptionInset();
        Root.SizeChanged += (_, _) => UpdateCaptionInset();

        if (withHomeTab)
        {
            AddHomeTab();
        }
    }

    public nint Hwnd { get; }

    private bool _active;

    /// <summary>A tool hit a bug: say so calmly in the window the user is working in.</summary>
    private void OnGuardFailed(object? sender, string what)
    {
        if (_active && Root.XamlRoot != null)
        {
            _ = Dialogs.ShowMessageAsync(Root.XamlRoot, "Something went wrong",
                "Papery hit a problem and stopped what it was doing. Your files have not been changed.");
        }
    }

    internal IEnumerable<DocumentView> Documents =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<DocumentView>();

    // ================================================================= opening files

    /// <summary>Opens PDFs in tabs. If the current tab is the home screen, the first file replaces it.</summary>
    public async Task OpenFilesAsync(IEnumerable<string> paths, ToolInfo? tool = null)
    {
        try
        {
            await WhenLoadedAsync(); // dialogs (password, errors) need the window to be on screen
            await OpenFilesCoreAsync(paths, tool);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Opening files failed", ex);
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Something went wrong", "Papery couldn't open the file. Your file has not been changed.");
        }
    }

    /// <summary>Completes once the window's content is loaded (immediately if it already is).</summary>
    private Task WhenLoadedAsync()
    {
        if (Root.XamlRoot != null && Root.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }

        Root.Loaded += OnLoaded;
        return loaded.Task;
    }

    private async Task OpenFilesCoreAsync(IEnumerable<string> paths, ToolInfo? tool)
    {
        var list = paths.ToList();
        var notPdf = list.Where(p => !DropFiles.IsPdf(p)).ToList();
        foreach (string path in list.Where(DropFiles.IsPdf))
        {
            // Two tabs on one file would each save over the other, so a file only ever opens once, in any window.
            string full = Path.GetFullPath(path);
            if (WindowManager.All.FirstOrDefault(w => w.ShowIfOpen(full)) is { } holder)
            {
                if (holder != this)
                {
                    holder.BringToFront();
                }

                continue;
            }

            var document = await DocumentOpener.OpenAsync(path, Root.XamlRoot);
            if (document == null)
            {
                continue;
            }

            var view = new DocumentView();
            AttachDocumentView(view);
            var tab = CreateTab(view, Path.GetFileName(path), "", path);
            if (Tabs.SelectedItem is TabViewItem { Content: HomeView } home)
            {
                int index = Tabs.TabItems.IndexOf(home);
                Tabs.TabItems.Insert(index, tab);
                Tabs.TabItems.Remove(home);
            }
            else
            {
                Tabs.TabItems.Add(tab);
            }

            view.Load(document); // before selecting, so the window title can show the file name
            Tabs.SelectedItem = tab;
            if (tool is { IsBuilt: true })
            {
                view.StartTool(tool);
            }
            else if (tool != null)
            {
                view.ShowNotice($"{tool.Name} is on its way", $"The {tool.Name} tool arrives in a later stage of this build. The file is open so you can read it.");
            }
        }

        // Pictures dropped on the window start a PDF of their own; other files can't be opened.
        var pictures = notPdf.Where(ImageFiles.IsImage).ToList();
        if (pictures.Count > 0 && await ProDialogs.RequireProAsync(Root.XamlRoot, ToolCatalog.All.First(t => t.Key == "images-to-pdf")))
        {
            await MakeNewPdfAsync("images-to-pdf", pictures);
        }

        var others = notPdf.Except(pictures).ToList();
        if (others.Count > 0)
        {
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Only PDFs and pictures open here", $"Not opened: {string.Join(", ", others.Select(Path.GetFileName))}");
        }
    }

    private bool ShowIfOpen(string fullPath)
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Content is DocumentView d && string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Tabs.SelectedItem = existing;
        }

        return existing != null;
    }

    public async Task PickAndOpenAsync(ToolInfo? tool = null)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await OpenFilesAsync(files.Select(f => f.Path), tool);
        }
    }

    /// <summary>Brings the window to the front (used when another Papery launch hands us a file).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        NativeMethods.SetForegroundWindow(Hwnd);
    }

    // ================================================================= tabs

    internal static TabViewItem CreateTab(UIElement content, string header, string glyph, string? tooltip = null)
    {
        var tab = new TabViewItem
        {
            Header = header,
            Content = content,
            IconSource = new FontIconSource { Glyph = glyph, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] },
        };
        ToolTipService.SetToolTip(tab, tooltip ?? header);
        return tab;
    }

    private void AddHomeTab()
    {
        var home = new HomeView();
        home.OpenRequested += (_, tool) => Guard.Run(async () =>
        {
            if (!await AllowToolAsync(tool))
            {
                return;
            }

            if (tool?.Key == "merge")
            {
                ShowMerge([]); // Merge has its own place to add files
            }
            else if (tool?.Key is "images-to-pdf" or "scan" or "batch" or "compare" or "html-to-pdf" or "text-to-pdf" or "search-files")
            {
                await MakeNewPdfAsync(tool.Key, []); // these don't start from one open PDF
            }
            else
            {
                await PickAndOpenAsync(tool);
            }
        }, "Opening a tool");
        home.FilesChosen += (_, e) => Guard.Run(async () =>
        {
            if (!await AllowToolAsync(e.Tool))
            {
                return;
            }

            if (e.Tool?.Key == "merge")
            {
                ShowMerge(e.Files);
            }
            else if (e.Tool?.Key is "images-to-pdf" or "scan" or "batch" or "compare" or "html-to-pdf" or "text-to-pdf" or "search-files")
            {
                await MakeNewPdfAsync(e.Tool.Key, e.Files);
            }
            else
            {
                await OpenFilesAsync(e.Files, e.Tool);
            }
        }, "Opening files");
        home.SettingsRequested += (_, _) => ShowSettings();
        home.HelpRequested += (_, _) => ShowHelp();
        var tab = CreateTab(home, "Home", "");
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    /// <summary>Free tools always pass. Pro tools show the unlock screen when Pro isn't unlocked yet.</summary>
    private async Task<bool> AllowToolAsync(ToolInfo? tool) =>
        tool is not { IsPro: true } || await ProDialogs.RequireProAsync(Root.XamlRoot, tool);

    /// <summary>Runs a tool that isn't tied to one open PDF. Used by the home screen and Explorer's right-click menu.</summary>
    public async Task MakeNewPdfAsync(string key, IReadOnlyList<string> pictures)
    {
        await WhenLoadedAsync();

        // Explorer's right-click menu comes straight here, without passing the home screen's check.
        if (!await AllowToolAsync(ToolCatalog.All.FirstOrDefault(t => t.Key == key)))
        {
            return;
        }

        if (key == "batch")
        {
            await Views.Tools.BatchDialog.ShowAsync(Root.XamlRoot, Hwnd, pictures);
            return;
        }

        if (key == "compare")
        {
            await ShowCompareAsync(pictures);
            return;
        }

        if (key == "search-files")
        {
            ShowFolderSearch();
            return;
        }

        if (key == "text-to-pdf")
        {
            if (await Views.Convert.TextToPdf.ShowAsync(Root.XamlRoot, Hwnd, pictures) is { } fromText)
            {
                await OpenFilesAsync([fromText]);
            }

            return;
        }

        if (key == "html-to-pdf")
        {
            if (await Views.Convert.HtmlToPdf.ShowAsync(Root.XamlRoot, Hwnd, pictures) is { } pdf)
            {
                await OpenFilesAsync([pdf]);
            }

            return;
        }

        string? made = key == "scan"
            ? await Views.Convert.ScanDialog.ShowAsync(Root.XamlRoot, Hwnd)
            : await Views.Convert.ImagesToPdfDialog.ShowAsync(Root.XamlRoot, Hwnd, pictures);
        if (made != null)
        {
            await OpenFilesAsync([made]);
        }
    }

    /// <summary>A tab for searching every PDF in a folder. Picking a result opens that page with the words found.</summary>
    private void ShowFolderSearch()
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is Views.Tools.FolderSearchView);
        if (existing != null)
        {
            Tabs.SelectedItem = existing;
            return;
        }

        var search = new Views.Tools.FolderSearchView(() => Hwnd);
        search.Picked += (_, pick) => Guard.Run(() => OpenAtAsync(pick.Hit.Path, pick.Hit.PageIndex, pick.Query), "Opening a result");
        var tab = CreateTab(search, "Search a folder", ToolCatalog.All.First(t => t.Key == "search-files").Glyph);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    private async Task OpenAtAsync(string path, int page, string query)
    {
        await OpenFilesAsync([path]);
        string full = Path.GetFullPath(path);
        var view = WindowManager.All.SelectMany(w => w.Documents).FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        view?.FindOnPage(page, query);
    }

    /// <summary>Compares two PDFs in a tab, asking for whichever of the two hasn't been given.</summary>
    private async Task ShowCompareAsync(IReadOnlyList<string> given)
    {
        var paths = given.Where(DropFiles.IsPdf).Take(2).ToList();
        while (paths.Count < 2)
        {
            var picked = await Pickers.PickPdfsAsync(Hwnd, multiple: paths.Count == 0);
            if (picked.Count == 0)
            {
                return;
            }

            paths.AddRange(picked.Where(DropFiles.IsPdf).Take(2 - paths.Count));
        }

        var left = await DocumentOpener.OpenAsync(paths[0], Root.XamlRoot);
        if (left == null)
        {
            return;
        }

        var right = await DocumentOpener.OpenAsync(paths[1], Root.XamlRoot);
        if (right == null)
        {
            await left.DisposeAsync();
            return;
        }

        var tab = CreateTab(new Views.Tools.CompareView(left, right), "Compare", "\uE8F1");
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    /// <summary>A tab moved onto a different file (Save a copy).</summary>
    private void UpdateTabFor(DocumentView view)
    {
        if (Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Content, view)) is { } tab)
        {
            tab.Header = view.FileName;
            ToolTipService.SetToolTip(tab, view.FilePath);
        }

        Title = $"{view.FileName} \u2013 Papery";
    }

    /// <summary>After a crash, offers back any unsaved work that was left behind.</summary>
    public async Task OfferRecoveryAsync()
    {
        var leftovers = RecoveryService.FindLeftovers();
        if (leftovers.Count == 0)
        {
            return;
        }

        await WhenLoadedAsync();
        string names = string.Join(", ", leftovers.Select(l => Path.GetFileName(l.OriginalPath)));
        var dialog = new ContentDialog
        {
            Title = "Bring back your unsaved work?",
            Content = Dialogs.Wrap($"Papery closed before saving your changes to: {names}."),
            PrimaryButtonText = "Restore",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        var answer = await Dialogs.ShowAsync(dialog, Root.XamlRoot);
        foreach (var leftover in leftovers)
        {
            if (answer == ContentDialogResult.Primary)
            {
                await RestoreLeftoverAsync(leftover);
            }
            else if (answer == ContentDialogResult.Secondary)
            {
                RecoveryService.Forget(leftover.CopyPath);
            }
        }
    }

    /// <summary>The work goes into a real file the person chose, and only then stops being kept for later.</summary>
    private async Task RestoreLeftoverAsync(RecoveredFile leftover)
    {
        string name = $"{Path.GetFileNameWithoutExtension(leftover.OriginalPath)} (recovered)";
        if (await Pickers.PickSavePdfAsync(Hwnd, name, leftover.OriginalPath) is not { } output)
        {
            return; // still kept, and offered again next time
        }

        try
        {
            File.Copy(leftover.CopyPath, output, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Restoring unsaved work failed", ex);
            Pickers.RemoveIfEmpty(output);
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Couldn't save there", "Your work is still safe, and Papery will offer it again next time.");
            return;
        }

        RecoveryService.Forget(leftover.CopyPath);
        await OpenFilesAsync([output]);
    }

    /// <summary>Opens a Merge tab, starting with <paramref name="paths"/>.</summary>
    public void ShowMerge(IReadOnlyList<string> paths)
    {
        var merge = new MergeView { GetWindowHandle = () => Hwnd };
        merge.OpenRequested += (_, path) => _ = OpenFilesAsync([path]);
        var tab = CreateTab(merge, "Merge", ToolCatalog.All.First(t => t.Key == "merge").Glyph);
        int at = Tabs.SelectedIndex >= 0 ? Tabs.SelectedIndex + 1 : Tabs.TabItems.Count;
        Tabs.TabItems.Insert(Math.Min(at, Tabs.TabItems.Count), tab);
        Tabs.SelectedItem = tab;
        if (paths.Count > 0)
        {
            _ = merge.AddFilesAsync(paths);
        }
    }

    private void ShowHelp()
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is HelpView);
        if (existing == null)
        {
            var help = new HelpView();
            help.HomeTourRequested += (_, _) => StartHomeTour();
            existing = CreateTab(help, "Help", "");
            Tabs.TabItems.Add(existing);
        }

        Tabs.SelectedItem = existing;
    }

    /// <summary>Shows the home tab (opening one if needed) and runs its tour once it's on screen.</summary>
    private void StartHomeTour()
    {
        var homeTab = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is HomeView);
        if (homeTab == null)
        {
            AddHomeTab();
            homeTab = (TabViewItem)Tabs.SelectedItem;
        }

        Tabs.SelectedItem = homeTab;
        var home = (HomeView)homeTab.Content;
        if (home.IsLoaded)
        {
            DispatcherQueue.TryEnqueue(home.StartTour);
        }
        else
        {
            void OnLoaded(object s, RoutedEventArgs e)
            {
                home.Loaded -= OnLoaded;
                DispatcherQueue.TryEnqueue(home.StartTour);
            }

            home.Loaded += OnLoaded;
        }
    }

    private void ShowSettings()
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Content is SettingsView);
        if (existing == null)
        {
            existing = CreateTab(new SettingsView(), "Settings", "");
            Tabs.TabItems.Add(existing);
        }

        Tabs.SelectedItem = existing;
    }

    /// <summary>Hooks a document view to whichever window it currently lives in (tabs can move between windows).</summary>
    private static void AttachDocumentView(DocumentView view)
    {
        view.GetWindowHandle = () => WindowManager.WindowFor(view)?.Hwnd ?? 0;
        view.PresentationRequested += (sender, enter) =>
        {
            WindowManager.WindowFor((DocumentView)sender!)?.SetPresenting((DocumentView)sender!, enter);
        };
        view.OpenFileRequested += (sender, path) => _ = WindowManager.WindowFor((DocumentView)sender!)?.OpenFilesAsync([path]);
        view.MergeRequested += (sender, path) => WindowManager.WindowFor((DocumentView)sender!)?.ShowMerge([path]);
        view.NewPdfMade += (sender, path) => _ = WindowManager.WindowFor((DocumentView)sender!)?.OpenFilesAsync([path]);
        view.CompareRequested += (sender, other) => _ = WindowManager.WindowFor((DocumentView)sender!)?.MakeNewPdfAsync("compare", [((DocumentView)sender!).FilePath, other]);
        view.BatchRequested += (sender, args) => _ = WindowManager.WindowFor((DocumentView)sender!)?.MakeNewPdfAsync("batch", [((DocumentView)sender!).FilePath]);
        view.FileChanged += (sender, args) => WindowManager.WindowFor((DocumentView)sender!)?.UpdateTabFor((DocumentView)sender!);
        view.NavigationRequested += (sender, target) =>
        {
            var window = WindowManager.WindowFor((DocumentView)sender!);
            if (target == "help")
            {
                window?.ShowHelp();
            }
            else
            {
                window?.ShowSettings();
            }
        };
    }

    private async Task CloseTabAsync(TabViewItem tab)
    {
        if (tab.Content is Views.Tools.CompareView compare)
        {
            await compare.CloseAsync();
        }

        if (tab.Content is Views.Tools.FolderSearchView folderSearch)
        {
            folderSearch.Stop();
        }

        if (tab.Content is DocumentView view)
        {
            Tabs.SelectedItem = tab;
            if (!await view.ConfirmCloseAsync())
            {
                return;
            }

            await view.CloseAsync();
        }
        else if (tab.Content is MergeView merge)
        {
            if (merge.IsBusy && !await Dialogs.ConfirmAsync(Root.XamlRoot, "Stop merging?", "The merge isn't finished. Nothing will be saved if you stop now.", "Stop", "Keep going"))
            {
                return;
            }

            await merge.CloseAsync();
        }

        Tabs.TabItems.Remove(tab);
        if (Tabs.TabItems.Count == 0)
        {
            if (WindowManager.Count > 1)
            {
                Close();
            }
            else
            {
                AddHomeTab();
            }
        }
    }

    private void OnAddTabClick(TabView sender, object args) => AddHomeTab();

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => _ = CloseTabAsync(args.Tab);

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Content: UIElement arriving })
        {
            Motion.FadeIn(arriving);
        }

        Title = Tabs.SelectedItem is TabViewItem { Content: DocumentView view } ? $"{view.FileName} – Papery" : "Papery";
    }

    // ---- tearing tabs out into new windows, and dropping them into other windows

    private void OnTabTearOutWindowRequested(TabView sender, TabViewTabTearOutWindowRequestedEventArgs args)
    {
        var window = WindowManager.Create(withHomeTab: false);
        args.NewWindowId = window.AppWindow.Id;
    }

    private void OnTabTearOutRequested(TabView sender, TabViewTabTearOutRequestedEventArgs args)
    {
        var target = WindowManager.Find(args.NewWindowId);
        if (target == null)
        {
            return;
        }

        foreach (var tab in args.Items.OfType<TabViewItem>().ToList())
        {
            Tabs.TabItems.Remove(tab);
            target.AdoptTab(tab, target.Tabs.TabItems.Count);
        }

        CloseIfEmpty();
    }

    private void OnExternalTabsDropping(TabView sender, TabViewExternalTornOutTabsDroppingEventArgs args) => args.AllowDrop = true;

    private void OnExternalTabsDropped(TabView sender, TabViewExternalTornOutTabsDroppedEventArgs args)
    {
        int index = args.DropIndex;
        foreach (var tab in args.Items.OfType<TabViewItem>().ToList())
        {
            var source = WindowManager.All.FirstOrDefault(w => w.Tabs.TabItems.Contains(tab));
            source?.Tabs.TabItems.Remove(tab);
            AdoptTab(tab, index++);
            source?.CloseIfEmpty();
        }
    }

    private void AdoptTab(TabViewItem tab, int index)
    {
        Tabs.TabItems.Insert(Math.Clamp(index, 0, Tabs.TabItems.Count), tab);
        Tabs.SelectedItem = tab;
    }

    private void CloseIfEmpty()
    {
        if (Tabs.TabItems.Count == 0)
        {
            DispatcherQueue.TryEnqueue(Close);
        }
    }

    // ================================================================= presentation (full screen)

    /// <summary>True if this window currently shows <paramref name="element"/> in one of its tabs.</summary>
    internal bool Hosts(UIElement element) =>
        Tabs.TabItems.OfType<TabViewItem>().Any(t => ReferenceEquals(t.Content, element));

    /// <summary>
    /// Presenting: the document stays in its tab (moving live UI between parents is fragile);
    /// the window goes full screen and the tab strip is hidden.
    /// </summary>
    internal void SetPresenting(DocumentView view, bool enter)
    {
        if (enter && !Hosts(view))
        {
            return;
        }

        var strip = FindByName(Tabs, "TabContainerGrid");
        if (strip != null)
        {
            strip.Visibility = enter ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            Tabs.Margin = enter ? new Thickness(0, -48, 0, 0) : default; // fallback: slide the strip off-screen
        }

        AppWindow.SetPresenter(enter ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } element && n == name)
            {
                return element;
            }

            if (FindByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ================================================================= drag and drop, shortcuts

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in Papery";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var files = await DropFiles.GetPathsAsync(e);
        if (files.Count > 0)
        {
            await OpenFilesAsync(files);
        }
    }

    private IReadOnlyList<AppCommand> GlobalCommands() =>
    [
        new("Open a PDF", "", () => _ = PickAndOpenAsync(), "Ctrl+O", "file browse load"),
        new("New tab", "", AddHomeTab, "Ctrl+T", "home tools"),
        new("Close tab", "", () => { if (Tabs.SelectedItem is TabViewItem t) { _ = CloseTabAsync(t); } }, "Ctrl+W", "close file"),
        new("Settings", "", ShowSettings, "Ctrl+,", "options preferences theme dark"),
        new("Help and keyboard shortcuts", "", ShowHelp, "F1", "keys tips how"),
        new("Tour of the home screen", "", StartHomeTour, Keywords: "help tutorial guide learn"),
    ];

    private void OnHelpShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { ShowHelp(); a.Handled = true; }

    private void OnOpenShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { _ = PickAndOpenAsync(); a.Handled = true; }

    private void OnNewTabShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { AddHomeTab(); a.Handled = true; }

    private void OnCloseTabShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (Tabs.SelectedItem is TabViewItem tab)
        {
            _ = CloseTabAsync(tab);
        }

        a.Handled = true;
    }

    private void OnNextTabShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Math.Max(1, Tabs.TabItems.Count);
        a.Handled = true;
    }

    private void OnPreviousTabShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + Tabs.TabItems.Count) % Math.Max(1, Tabs.TabItems.Count);
        a.Handled = true;
    }

    private void OnCommandPaletteShortcut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        var commands = new List<AppCommand>(GlobalCommands());
        if (Tabs.SelectedItem is TabViewItem { Content: DocumentView view })
        {
            commands.InsertRange(0, view.GetCommands());
        }

        _ = CommandPalette.ShowAsync(Root.XamlRoot, commands);
        a.Handled = true;
    }

    // ================================================================= look and feel

    private void ApplyBackdrop()
    {
        // Papery uses its own warm, solid window colour (Root's Background in MainWindow.xaml) rather than
        // Windows' Mica, so it has its own look and is the same on Windows 10 and 11.

        // Ctrl+, opens Settings (XAML can't name the comma key).
        var settings = new KeyboardAccelerator { Key = (Windows.System.VirtualKey)188, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        settings.Invoked += (_, a) =>
        {
            ShowSettings();
            a.Handled = true;
        };
        Root.KeyboardAccelerators.Add(settings);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyTheme);

    private void ApplyTheme()
    {
        Root.RequestedTheme = AppServices.Settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // The minimise/maximise/close buttons are drawn by Windows: give them matching colours.
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0, 0, 0);
        titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0, 0, 0);
        Root.ActualThemeChanged -= OnActualThemeChanged;
        Root.ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void UpdateCaptionInset()
    {
        // Leave room on the right of the tab strip for the caption buttons.
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        DragRegion.MinWidth = (AppWindow.TitleBar.RightInset / scale) + 48;
    }

    private void RestoreSize()
    {
        var s = AppServices.Settings.Current;
        if (s.WindowWidth >= 640 && s.WindowHeight >= 480 && WindowManager.Count == 0)
        {
            AppWindow.Resize(new SizeInt32(s.WindowWidth, s.WindowHeight));
        }
        else if (WindowManager.Count == 0)
        {
            // First run: this is the size it returns to from maximised: 75% x 85% of the screen, centred.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = (int)(area.Width * 0.75), h = (int)(area.Height * 0.85);
            AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 420;
            if (s.OpenMaximized && WindowManager.Count == 0)
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>Closing: save every document's position first (needs async, so cancel once and close again).</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }

        // Stays cancelled until everything has been asked, so a second click on X can't skip "Save your changes?".
        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (!await ConfirmClosingAsync())
            {
                _closing = false;
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Asking about unsaved work failed", ex);
            _closing = false;
            return;
        }

        try
        {
            await FinishClosingAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Tidying up while closing failed", ex);
        }

        _closeApproved = true;
        Close();
    }

    private async Task<bool> ConfirmClosingAsync()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            bool maximized = presenter.State == OverlappedPresenterState.Maximized;
            var size = AppWindow.Size;
            AppServices.Settings.Update(s =>
            {
                s.OpenMaximized = maximized;
                if (!maximized)
                {
                    s.WindowWidth = size.Width;
                    s.WindowHeight = size.Height;
                }
            });
        }

        foreach (var view in Documents.ToList())
        {
            await view.FlushFormAsync(); // the last words typed may be the only change
        }

        // Unsaved page changes: show each tab and ask; Cancel keeps the window open.
        var asked = new List<DocumentView>();
        foreach (var view in Documents.Where(d => d.HasUnsavedChanges).ToList())
        {
            Tabs.SelectedItem = Tabs.TabItems.OfType<TabViewItem>().First(t => ReferenceEquals(t.Content, view));
            if (!await view.ConfirmCloseAsync())
            {
                foreach (var earlier in asked)
                {
                    await earlier.CloseCancelledAsync();
                }

                return false;
            }

            asked.Add(view);
        }

        var merges = Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<MergeView>().ToList();
        return !merges.Any(m => m.IsBusy)
            || await Dialogs.ConfirmAsync(Root.XamlRoot, "Stop merging?", "The merge isn't finished. Nothing will be saved if you stop now.", "Stop", "Keep going");
    }

    private async Task FinishClosingAsync()
    {
        var contents = Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).ToList();
        foreach (var content in contents)
        {
            switch (content)
            {
                case DocumentView view:
                    await view.CloseAsync();
                    break;
                case MergeView merge:
                    await merge.CloseAsync();
                    break;
                case Views.Tools.CompareView compare:
                    await compare.CloseAsync();
                    break;
                case Views.Tools.FolderSearchView folderSearch:
                    folderSearch.Stop();
                    break;
            }
        }

        await AppServices.ViewState.SaveAsync();
        if (WindowManager.Count <= 1)
        {
            TempFiles.ClearAttachments();
        }
    }
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
}
