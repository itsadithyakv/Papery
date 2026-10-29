using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Core.Pages;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Papery.Views;

/// <summary>One file waiting to be merged.</summary>
public sealed partial class MergeFile : ObservableObject
{
    internal MergeFile(PdfDocument document)
    {
        Document = document;
        Path = document.FilePath;
        Name = System.IO.Path.GetFileName(document.FilePath);
    }

    internal PdfDocument Document { get; }

    public string Path { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    /// <summary>The pages to include as typed ("1-3, 5"); empty means all.</summary>
    [ObservableProperty]
    public partial string Pages { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    public string Info
    {
        get
        {
            int count = Document.PageCount;
            string all = count == 1 ? "1 page" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} pages";
            return TryGetPages(out var pages, out _) && pages.Count != count ? $"{pages.Count:N0} of {all}" : all;
        }
    }

    public string AccessibleName => $"{Name}, {Info}";

    /// <summary>The chosen pages (0-based), or false with a friendly error.</summary>
    public bool TryGetPages(out IReadOnlyList<int> pages, out string? error)
    {
        if (string.IsNullOrWhiteSpace(Pages))
        {
            pages = Enumerable.Range(0, Document.PageCount).ToArray();
            error = null;
            return true;
        }

        return PageRanges.TryParse(Pages, Document.PageCount, out pages, out error);
    }

    partial void OnPagesChanged(string value)
    {
        Error = TryGetPages(out _, out string? error) ? string.Empty : error ?? string.Empty;
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(AccessibleName));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>Combines several PDFs into one new file, in the order shown.</summary>
public sealed partial class MergeView : UserControl
{
    private readonly ObservableCollection<MergeFile> _files = [];
    private CancellationTokenSource? _busy;

    public MergeView()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
        _files.CollectionChanged += (_, _) => Update();
        Update();
    }

    /// <summary>Asks the window to open the merged file.</summary>
    public event EventHandler<string>? OpenRequested;

    internal Func<nint> GetWindowHandle { get; set; } = () => 0;

    public bool IsBusy => _busy != null;

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        await WhenLoadedAsync(); // password and error dialogs need the view on screen
        foreach (string path in paths)
        {
            if (!DropFiles.IsPdf(path))
            {
                continue;
            }

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

            var file = new MergeFile(document);
            file.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MergeFile.Pages))
                {
                    Update();
                }
            };
            _files.Add(file);
            _ = LoadThumbnailAsync(file);
        }

        if (_files.Count > 0)
        {
            Character.Bounce();
        }
    }

    public async Task CloseAsync()
    {
        _busy?.Cancel();
        foreach (var file in _files.ToList())
        {
            await file.Document.DisposeAsync();
        }

        _files.Clear();
    }

    private Task WhenLoadedAsync()
    {
        if (IsLoaded && XamlRoot != null)
        {
            return Task.CompletedTask;
        }

        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            loaded.TrySetResult();
        }

        Loaded += OnLoaded;
        return loaded.Task;
    }

    private async Task LoadThumbnailAsync(MergeFile file)
    {
        try
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            var size = file.Document.PageSizes[0];
            int width = (int)(46 * scale), height = Math.Max(16, (int)(width * size.Height / size.Width));
            var bitmap = await file.Document.RenderPageAsync(0, width, height, PdfRenderOptions.Annotations, PdfWorkPriority.Nearby);
            file.Thumbnail = PageBitmaps.ToWriteableBitmap(bitmap);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
            // No preview; the file can still be merged (or will explain itself when it can't).
        }
    }

    private void Update()
    {
        bool any = _files.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = Options.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        int pages = _files.Sum(f => f.TryGetPages(out var p, out _) ? p.Count : 0);
        string files = _files.Count == 1 ? "1 file" : $"{_files.Count} files";
        TotalText.Text = any ? $"{files}  ·  {pages:N0} pages" : string.Empty;
        MergeButton.IsEnabled = _busy == null && _files.Count >= 2 && _files.All(f => !f.HasError) && pages > 0;
    }

    private async Task MergeAsync()
    {
        // Merge can be reached without the home screen (Ctrl+K, Explorer), so the Pro check lives here too.
        if (!await ProDialogs.RequireProAsync(XamlRoot, Commands.ToolCatalog.All.First(t => t.Key == "merge")))
        {
            return;
        }

        // Gather each file's pages, and its bookmarks moved to where its pages land.
        var sources = new List<PdfInputFile>();
        var pages = new List<PageRef>();
        var outline = new List<PdfOutlineItem>();
        foreach (var file in _files)
        {
            if (!file.TryGetPages(out var chosen, out _))
            {
                return;
            }

            int source = sources.Count, start = pages.Count;
            sources.Add(new PdfInputFile(file.Path, file.Document.Password));
            pages.AddRange(chosen.Select(p => new PageRef(source, p)));

            var own = KeepBookmarks.IsOn ? await file.Document.GetOutlineAsync() : [];
            var firstAt = new Dictionary<int, int>();
            for (int i = 0; i < chosen.Count; i++)
            {
                firstAt.TryAdd(chosen[i], start + i);
            }

            var moved = SplitPlanner.RemapOutline(own, p => firstAt.TryGetValue(p, out int at) ? at : null);
            if (BookmarkPerFile.IsOn)
            {
                outline.Add(new PdfOutlineItem(System.IO.Path.GetFileNameWithoutExtension(file.Name), start, moved));
            }
            else
            {
                outline.AddRange(moved);
            }
        }

        var first = _files[0];
        string suggested = $"{System.IO.Path.GetFileNameWithoutExtension(first.Name)} (merged).pdf";
        string? output = await Pickers.PickSavePdfAsync(GetWindowHandle(), suggested, first.Path);
        if (output == null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _busy = cts;
        BusyProgress.Value = 0;
        BusyOverlay.Visibility = Visibility.Visible;
        Update();
        try
        {
            var request = new PdfComposeRequest(sources, pages, Outline: outline);
            var result = await PdfEngine.Shared.ComposeAsync(request, output, new Progress<double>(v => BusyProgress.Value = v), cts.Token);
            Character.Mood = MascotMood.Excited;
            Character.Bounce();
            string message = $"Merged into {System.IO.Path.GetFileName(output)}";
            Toaster.Show(result.BookmarksSkipped ? message + " (without bookmarks)" : message, "", "Open", () => OpenRequested?.Invoke(this, output));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
        }
        finally
        {
            Pickers.RemoveIfEmpty(output);
            _busy = null;
            BusyOverlay.Visibility = Visibility.Collapsed;
            Update();
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        var paths = await Pickers.PickPdfsAsync(GetWindowHandle());
        await AddFilesAsync(paths);
    }, "OnAddClick");

    private void OnMergeClick(object sender, RoutedEventArgs e) => Guard.Run(() => MergeAsync(), "OnMergeClick");

    private void OnCancelClick(object sender, RoutedEventArgs e) => _busy?.Cancel();

    private void OnRemoveClick(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        if (((FrameworkElement)sender).Tag is MergeFile file && _busy == null)
        {
            _files.Remove(file);
            await file.Document.DisposeAsync();
        }
    }, "OnRemoveClick");

    private void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Update();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && _busy == null)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to the merge";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e) => Guard.Run(async () =>
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || _busy != null)
        {
            return;
        }

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

        await AddFilesAsync(paths);
    }, "OnDrop");
}
