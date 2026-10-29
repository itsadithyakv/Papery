using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Commands;
using Papery.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Papery.Views;

public sealed class RecentFileItem(string path)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>One tip on the "Getting started" card.</summary>
public sealed record TipItem(string Glyph, string Title, string Text)
{
    public string AccessibleName => $"{Title}. {Text}";
}

/// <summary>The "All tools" home screen shown when no file is open.</summary>
public sealed partial class HomeView : UserControl
{
    private static readonly IReadOnlyList<TipItem> Tips =
    [
        new("", "Open anything", "Drop PDFs anywhere in the window, or press Ctrl+O."),
        new("", "Find in seconds", "Ctrl+F searches every page; F3 jumps to the next match."),
        new("", "Just type it", "Ctrl+K opens the command palette: try \"dark\", \"print\" or \"zoom\"."),
        new("", "Read comfortably", "Dark and Sepia page colours keep pictures looking right."),
    ];

    public HomeView()
    {
        InitializeComponent();
        ToolGrid.ItemsSource = ToolCatalog.All;
        TipsList.ItemsSource = Tips;
        Loaded += (_, _) =>
        {
            RefreshRecent();
            GettingStarted.Visibility = AppServices.Settings.Current.ShowGettingStarted ? Visibility.Visible : Visibility.Collapsed;
            AppServices.ViewState.RecentFilesChanged += OnRecentFilesChanged;
            OpenButton.Focus(FocusState.Programmatic);
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
            new(() => OpenButton, "Open a PDF", "Start here, or drag PDFs straight into the window. You can open several; each gets its own tab."),
            new(() => DropZone, "Drop files here", "Dropping a PDF onto a tool card below opens it ready for that tool."),
            new(() => RecentSection, "Pick up where you left off", "Recent files reopen at the same page and zoom. Right-click one to remove it."),
            new(() => ToolGrid, "Every tool in one place", "Merge, split, compress, sign and more. Tools with a small lock are part of Papery Pro."),
            new(() => HelpButton, "Help is always here", "Keyboard shortcuts and this tour (F1). Once a PDF is open, More (⋯) › Take the tour shows the viewer."),
        };
        new GuidedTour((Panel)Content, steps).Start();
    }

    private void OnRecentFilesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshRecent);

    private void RefreshRecent()
    {
        var items = AppServices.ViewState.RecentFiles.Select(p => new RecentFileItem(p)).ToList();
        RecentList.ItemsSource = items;
        RecentSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnRecentClick(object sender, ItemClickEventArgs e) =>
        FilesChosen?.Invoke(this, ([((RecentFileItem)e.ClickedItem).Path], null));

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
        var files = await DropFiles.GetPathsAsync(e);
        if (files.Count > 0)
        {
            FilesChosen?.Invoke(this, (files, null));
        }
    }

    private async void OnToolDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; // don't also trigger the page-wide drop
        var files = await DropFiles.GetPathsAsync(e);
        if (files.Count > 0)
        {
            FilesChosen?.Invoke(this, (files, (ToolInfo)((FrameworkElement)sender).Tag));
        }
    }
}
