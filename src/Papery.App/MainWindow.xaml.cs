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
            var existing = Tabs.TabItems.OfType<TabViewItem>()
                .FirstOrDefault(t => t.Content is DocumentView d && string.Equals(d.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Tabs.SelectedItem = existing;
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
            if (tool is { IsBuilt: false })
            {
                view.ShowNotice($"{tool.Name} is on its way", $"The {tool.Name} tool arrives in a later stage of this build. The file is open so you can read it.");
            }
        }

        if (notPdf.Count > 0)
        {
            await Dialogs.ShowMessageAsync(Root.XamlRoot, "Only PDF files open here",
                $"Papery opens PDF files. Turning images into a PDF arrives with the Convert tools.\n\nNot opened: {string.Join(", ", notPdf.Select(Path.GetFileName))}");
        }
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
        home.OpenRequested += async (_, tool) =>
        {
            if (await AllowToolAsync(tool))
            {
                await PickAndOpenAsync(tool);
            }
        };
        home.FilesChosen += async (_, e) =>
        {
            if (await AllowToolAsync(e.Tool))
            {
                await OpenFilesAsync(e.Files, e.Tool);
            }
        };
        home.SettingsRequested += (_, _) => ShowSettings();
        home.HelpRequested += (_, _) => ShowHelp();
        var tab = CreateTab(home, "Home", "");
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    /// <summary>Free tools always pass. Pro tools show the unlock screen when Pro isn't unlocked yet.</summary>
    private async Task<bool> AllowToolAsync(ToolInfo? tool) =>
        tool is not { IsPro: true } || await ProDialogs.RequireProAsync(Root.XamlRoot, tool);

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
        if (tab.Content is DocumentView view)
        {
            await view.CloseAsync();
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
            // First run: a comfortable 75% x 85% of the screen, centred.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = (int)(area.Width * 0.75), h = (int)(area.Height * 0.85);
            AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 560;
            presenter.PreferredMinimumHeight = 420;
            if (s.WindowMaximized && WindowManager.Count == 0)
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>Closing: save every document's position first (needs async, so cancel once and close again).</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing)
        {
            return;
        }

        args.Cancel = true;
        _closing = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            bool maximized = presenter.State == OverlappedPresenterState.Maximized;
            var size = AppWindow.Size;
            AppServices.Settings.Update(s =>
            {
                s.WindowMaximized = maximized;
                if (!maximized)
                {
                    s.WindowWidth = size.Width;
                    s.WindowHeight = size.Height;
                }
            });
        }

        foreach (var view in Documents.ToList())
        {
            await view.CloseAsync();
        }

        await AppServices.ViewState.SaveAsync();
        Close();
    }
}

internal static partial class NativeMethods
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
}
