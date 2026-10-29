using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Commands;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views;

/// <summary>Modes and the page tools (Organise, Split) of a document tab.</summary>
public sealed partial class DocumentView
{
    private const int ViewModeIndex = 0;
    private const int OrganiseModeIndex = 1;

    private OrganiseView? _organise;
    private int _modeIndex = ViewModeIndex;
    private bool _settingMode;
    private bool _closingTab;

    /// <summary>Asks the window to open a PDF in a new tab.</summary>
    public event EventHandler<string>? OpenFileRequested;

    /// <summary>Asks the window to start a merge that begins with this file.</summary>
    public event EventHandler<string>? MergeRequested;

    /// <summary>True while Organise mode holds changes that haven't been saved.</summary>
    public bool HasUnsavedChanges => _organise?.HasChanges == true;

    /// <summary>Opens a tool on this document, e.g. when it was picked on the home screen.</summary>
    public void StartTool(ToolInfo tool)
    {
        if (!IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                Loaded -= OnLoaded;
                DispatcherQueue.TryEnqueue(() => StartTool(tool));
            }

            Loaded += OnLoaded;
            return;
        }

        switch (tool.Key)
        {
            case "organise":
                Modes.SelectedIndex = OrganiseModeIndex;
                break;
            case "split":
                _ = ShowSplitAsync();
                break;
        }
    }

    /// <summary>Called before the tab closes. Returns false if the user wants to keep it open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (_organise == null)
        {
            return true;
        }

        _closingTab = true;
        bool close = await _organise.ConfirmLeaveAsync();
        _closingTab = close;
        return close;
    }

    // ================================================================= modes

    private async void OnModeSelected(int index, string name)
    {
        if (_settingMode || index == _modeIndex)
        {
            return;
        }

        try
        {
            await SwitchModeAsync(index, name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A bug in a tool must never take the open document down with it.
            Log.Error($"Switching to {name} failed", ex);
            SetModeSilently(ViewModeIndex);
            ShowNotice("That didn't work", $"Papery couldn't open {name}. Your file has not been changed.", InfoBarSeverity.Error);
        }
    }

    private async Task SwitchModeAsync(int index, string name)
    {
        // Leaving Organise asks about unsaved changes first.
        if (_organise != null)
        {
            if (!await _organise.ConfirmLeaveAsync())
            {
                SetModeSilently(OrganiseModeIndex);
                return;
            }

            await ExitOrganiseAsync(saved: false); // nothing to do if "Save" already closed it
        }

        if (index == OrganiseModeIndex && !await EnterOrganiseAsync())
        {
            SetModeSilently(ViewModeIndex);
            index = ViewModeIndex;
        }

        _modeIndex = index;
        ApplyModeChrome(index, name);
    }

    private void SetModeSilently(int index)
    {
        _settingMode = true;
        Modes.SelectedIndex = index;
        _settingMode = false;
        _modeIndex = index;
        ApplyModeChrome(index, index == ViewModeIndex ? "View" : "Organise");
    }

    private void ApplyModeChrome(int index, string name)
    {
        if (index == OrganiseModeIndex)
        {
            return; // the Organise board covers the toolbar and pages
        }

        bool view = index == ViewModeIndex;
        ViewToolbar.Visibility = view ? Visibility.Visible : Visibility.Collapsed;
        ComingSoonText.Visibility = view ? Visibility.Collapsed : Visibility.Visible;
        ComingSoonText.Text = $"The {name} tools arrive in a later stage of this build.";
    }

    // ================================================================= organise

    private async Task<bool> EnterOrganiseAsync()
    {
        if (_document == null || IsPresenting || XamlRoot == null)
        {
            return false;
        }

        if (!await ProDialogs.RequireProAsync(XamlRoot, Tool("organise")) || !await CheckCanChangeAsync())
        {
            return false;
        }

        var view = new OrganiseView { GetWindowHandle = () => GetWindowHandle?.Invoke() ?? 0 };
        Grid.SetRow(view, 2); // the toolbar row above collapses, so the board gets all the height
        view.Initialize(_document, Viewer.ReadingMode);
        view.Closed += OnOrganiseClosed;
        view.OpenRequested += (_, path) => OpenFileRequested?.Invoke(this, path);
        LayoutRoot.Children.Add(view);
        ToolbarArea.Visibility = Visibility.Collapsed;
        ReadingArea.Visibility = Visibility.Collapsed;
        SetViewShortcutsEnabled(false);
        Notice.IsOpen = false;
        _organise = view;
        Motion.Entrance(view, 0, 10);
        return true;
    }

    private void OnOrganiseClosed(object? sender, OrganiseClosed e)
    {
        // Back to View straight away (before any await), so a mode picked meanwhile wins.
        SetModeSilently(ViewModeIndex);
        Guard.Run(() => ExitOrganiseAsync(e.Saved), "Leaving Organise");
    }

    private async Task ExitOrganiseAsync(bool saved)
    {
        var view = _organise;
        if (view == null)
        {
            return;
        }

        _organise = null;
        view.Closed -= OnOrganiseClosed;
        LayoutRoot.Children.Remove(view);
        ToolbarArea.Visibility = Visibility.Visible;
        ReadingArea.Visibility = Visibility.Visible;
        Viewer.Redraw();
        SetViewShortcutsEnabled(true);
        await view.CloseAsync();
        if (saved && !_closingTab)
        {
            await ReloadAsync();
            Toaster.Show("Saved", "");
        }

        if (!_closingTab)
        {
            Viewer.FocusPages();
        }
    }

    /// <summary>The viewer's own shortcuts (Ctrl+F, Ctrl+P...) are off while the Organise board has its own.</summary>
    private void SetViewShortcutsEnabled(bool enabled)
    {
        foreach (var accelerator in KeyboardAccelerators)
        {
            accelerator.IsEnabled = enabled;
        }
    }

    /// <summary>Opens the file again after it was saved, keeping the place in it.</summary>
    private async Task ReloadAsync()
    {
        var old = _document;
        if (old == null)
        {
            return;
        }

        SaveState(); // remembers the current page, which Load restores
        PdfDocument fresh;
        try
        {
            fresh = await PdfEngine.Shared.OpenAsync(FilePath, old.Password);
        }
        catch (PdfOpenException ex)
        {
            Log.Error("Reload after save failed", ex);
            ShowNotice("Saved, but couldn't show the new version", "Close this tab and open the file again to see your changes.", InfoBarSeverity.Warning);
            return;
        }

        _searchCts?.Cancel();
        _results.Clear();
        _searchedQuery = string.Empty;
        _currentResult = -1;
        SearchStatus.Text = string.Empty;
        _thumbnails.Clear();
        _thumbnailCache.Clear();
        _document = null;
        Load(fresh);
        await old.DisposeAsync();
    }

    // ================================================================= split

    private async Task ShowSplitAsync()
    {
        if (_document == null || XamlRoot == null)
        {
            return;
        }

        if (!await ProDialogs.RequireProAsync(XamlRoot, Tool("split")) || !await CheckCanChangeAsync())
        {
            return;
        }

        if (await SplitDialog.ShowAsync(_document, XamlRoot, GetWindowHandle?.Invoke() ?? 0) is { Files.Count: > 0 } outcome)
        {
            Toaster.Show(SplitDialog.Describe(outcome), "", "Show", () => Shell.ShowInFolder(outcome.Files[0]));
        }
    }

    /// <summary>Explains (once, kindly) when the PDF's author has switched off page changes.</summary>
    private async Task<bool> CheckCanChangeAsync()
    {
        if (_document is { CanAssemble: true })
        {
            return true;
        }

        var (title, message) = EditErrors.Explain(new PdfEditException(PdfEditError.NotAllowed, FilePath));
        await Dialogs.ShowMessageAsync(XamlRoot, title, message, Controls.MascotMood.Oops);
        return false;
    }

    private static ToolInfo Tool(string key) => ToolCatalog.All.First(t => t.Key == key);

    private IEnumerable<AppCommand> PageToolCommands()
    {
        bool hasDoc() => _document != null;
        return
        [
            new("Organise pages", Tool("organise").Glyph, () => Modes.SelectedIndex = OrganiseModeIndex, Keywords: "reorder rotate turn delete remove move pages", CanExecute: hasDoc),
            new("Split into several PDFs", Tool("split").Glyph, () => _ = ShowSplitAsync(), Keywords: "separate chapters break apart", CanExecute: hasDoc),
            new("Merge with other PDFs", Tool("merge").Glyph, () => MergeRequested?.Invoke(this, FilePath), Keywords: "combine join append", CanExecute: hasDoc),
        ];
    }
}
