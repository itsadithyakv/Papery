using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Commands;
using Papery.Controls;
using Papery.Pdf;
using Papery.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Papery.Views;

/// <summary>A recent file, shown as a little preview of its first page.</summary>
public sealed partial class RecentFileItem(string path) : ObservableObject
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    /// <summary>Shown instead of a preview: a lock (password) or a warning (file missing). Empty normally.</summary>
    [ObservableProperty]
    public partial string StatusGlyph { get; set; } = string.Empty;
}

/// <summary>The home screen shown when no file is open.</summary>
public sealed partial class HomeView : UserControl
{
    private const int MaxRecentPreviews = 10;

    // (keys, what they do) for the shortcut chips. Keys are drawn as keycaps, not written out.
    private static readonly (string[] Keys, string Label)[] Shortcuts =
    [
        (["Ctrl", "O"], "Open"),
        (["Ctrl", "F"], "Find"),
        (["Ctrl", "K"], "Do anything"),
        (["F5"], "Present"),
        (["F1"], "Help"),
    ];

    private readonly HashSet<UIElement> _animated = [];
    private bool _entranceDone;

    public HomeView()
    {
        InitializeComponent();
        ToolGrid.ItemsSource = ToolCatalog.All;
        Greeting.Text = DateTime.Now.Hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        foreach (var key in new[] { "Ctrl", "O" })
        {
            DropKeys.Children.Add(Keycap(key));
        }

        foreach (var (keys, label) in Shortcuts)
        {
            ShortcutChips.Children.Insert(ShortcutChips.Children.Count - 1, Chip(keys, label));
        }

        Loaded += (_, _) =>
        {
            RefreshRecent();
            GettingStarted.Visibility = AppServices.Settings.Current.ShowGettingStarted ? Visibility.Visible : Visibility.Collapsed;
            AppServices.ViewState.RecentFilesChanged += OnRecentFilesChanged;
            OpenButton.Focus(FocusState.Programmatic);
            if (!_entranceDone)
            {
                _entranceDone = true;
                Motion.Entrance(Hero);
                Motion.Entrance(DropZone, 60);
                Motion.Entrance(GettingStarted, 110);
                Motion.Entrance(RecentSection, 160);
            }
        };
        Unloaded += (_, _) => AppServices.ViewState.RecentFilesChanged -= OnRecentFilesChanged;
    }

    /// <summary>The user wants to choose a file to open (optionally with a tool in mind).</summary>
    public event EventHandler<ToolInfo?>? OpenRequested;

    /// <summary>Files were dropped or picked from Recent (optionally onto a tool card).</summary>
    public event EventHandler<(IReadOnlyList<string> Files, ToolInfo? Tool)>? FilesChosen;

    public event EventHandler? SettingsRequested;

    public event EventHandler? HelpRequested;

    /// <summary>Starts the home screen tour (also used from the Help page).</summary>
    public void StartTour()
    {
        var steps = new List<GuidedTour.Step>
        {
            new(() => OpenButton, "Open a PDF", "Start here, or drag PDFs straight into the window. Each file gets its own tab."),
            new(() => DropZone, "Drop files here", "Drop a PDF onto a tool card below to open it ready for that tool."),
            new(() => RecentSection, "Pick up where you left off", "Recent files reopen at the same page and zoom."),
            new(() => ToolGrid, "Every tool in one place", "Tools with a small lock are part of Papery Pro."),
            new(() => HelpButton, "Help is always here", "Keyboard shortcuts and tours live here (F1)."),
        };
        new GuidedTour((Panel)Content, steps).Start();
    }

    // ================================================================= recent files

    private void OnRecentFilesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshRecent);

    private void RefreshRecent()
    {
        var items = AppServices.ViewState.RecentFiles.Take(MaxRecentPreviews).Select(p => new RecentFileItem(p)).ToList();
        RecentList.ItemsSource = items;
        RecentSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items)
        {
            _ = LoadPreviewAsync(item);
        }
    }

    /// <summary>Renders the first page small. Locked or missing files get an icon instead.</summary>
    private async Task LoadPreviewAsync(RecentFileItem item)
    {
        if (!File.Exists(item.Path))
        {
            item.StatusGlyph = ""; // warning: moved or deleted
            return;
        }

        try
        {
            await using var document = await PdfEngine.Shared.OpenAsync(item.Path);
            var size = document.PageSizes[0];
            double scale = XamlRoot?.RasterizationScale ?? 1;
            int w = (int)(120 * scale);
            int h = Math.Clamp((int)(w * size.Height / size.Width), 16, (int)(140 * scale));
            var bitmap = await document.RenderPageAsync(0, w, h, PdfRenderOptions.Annotations, PdfWorkPriority.Background);
            item.Thumbnail = PageBitmaps.ToWriteableBitmap(bitmap);
        }
        catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired)
        {
            item.StatusGlyph = ""; // lock
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            item.StatusGlyph = ""; // plain document icon
        }
    }

    private void OnRecentPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_animated.Add(args.Element) && args.Element is Control card)
        {
            HighlightOnHover(card, null);
            Motion.Entrance(card, 180 + (Math.Min(args.Index, 8) * 30));
        }
    }

    private void OnRecentClick(object sender, RoutedEventArgs e) =>
        FilesChosen?.Invoke(this, ([(string)((FrameworkElement)sender).Tag], null));

    private void OnRemoveRecent(object sender, RoutedEventArgs e)
    {
        AppServices.ViewState.RemoveRecent((string)((FrameworkElement)sender).Tag);
        _ = AppServices.ViewState.SaveAsync();
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        AppServices.ViewState.ClearRecent();
        _ = AppServices.ViewState.SaveAsync();
    }

    // ================================================================= tools and buttons

    private void OnToolPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_animated.Add(args.Element) && args.Element is Control card)
        {
            HighlightOnHover(card, ((ToolInfo)card.Tag).TintBrush);
            Motion.Entrance(card, 220 + (Math.Min(args.Index, 16) * 22));
        }
    }

    /// <summary>
    /// Calm hover: the card doesn't move, its outline just takes the tool's colour (coral when null).
    /// Clearing the local value hands the border back to the theme style.
    /// </summary>
    private static void HighlightOnHover(Control card, Brush? tint)
    {
        card.PointerEntered += (_, _) => card.BorderBrush = tint ?? ThemeResources.Brush(card, "PaperyCoralBrush");
        card.PointerExited += (_, _) => card.ClearValue(Control.BorderBrushProperty);
        card.PointerCanceled += (_, _) => card.ClearValue(Control.BorderBrushProperty);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, null);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnHelpClick(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);

    private void OnTourClick(object sender, RoutedEventArgs e) => StartTour();

    private void OnHideTipsClick(object sender, RoutedEventArgs e)
    {
        GettingStarted.Visibility = Visibility.Collapsed;
        AppServices.Settings.Update(s => s.ShowGettingStarted = false);
    }

    private void OnToolClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, (ToolInfo)((FrameworkElement)sender).Tag);

    // ================================================================= drag and drop (the character reacts)

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        Character.Mood = MascotMood.Excited;
        Character.Bounce();
        Motion.Pop(DropIcon, 1.18f);
        DropTitle.Text = "Let go to open";
        DropOutline.StrokeThickness = 2.5;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void ResetDropZone()
    {
        Character.Mood = MascotMood.Happy;
        DropTitle.Text = "Drop PDFs here";
        DropOutline.StrokeThickness = 1.5;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = sender is FrameworkElement { Tag: ToolInfo tool } ? tool.Name : "Open in Papery";
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        var files = await DropFiles.GetPathsAsync(e);
        if (files.Count > 0)
        {
            FilesChosen?.Invoke(this, (files, null));
        }
    }

    private async void OnToolDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; // don't also trigger the page-wide drop
        ResetDropZone();
        var files = await DropFiles.GetPathsAsync(e);
        if (files.Count > 0)
        {
            FilesChosen?.Invoke(this, (files, (ToolInfo)((FrameworkElement)sender).Tag));
        }
    }

    // ================================================================= little visual pieces

    /// <summary>A keyboard key drawn as a keycap (slightly thicker bottom edge, like a real key).</summary>
    internal static Border Keycap(string key) => new()
    {
        Child = new TextBlock { Text = key, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
        Style = (Style)Application.Current.Resources["PaperyKeycapStyle"], // a style, so colours follow the theme
    };

    private static Border Chip(string[] keys, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (string key in keys)
        {
            row.Children.Add(Keycap(key));
        }

        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Child = row, Style = (Style)Application.Current.Resources["PaperyChipStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"{string.Join(" ", keys)}: {label}");
        return chip;
    }
}
