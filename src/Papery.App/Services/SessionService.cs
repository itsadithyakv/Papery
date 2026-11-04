using Papery.Views;

namespace Papery.Services;

/// <summary>
/// Remembers which PDFs are open, in tab order, and opens them again next time, each at the page it was left on
/// (that part is the per-file position Papery already keeps). Like a browser picking up where it left off.
/// </summary>
internal static class SessionService
{
    private static bool _restoring;

    // Nothing is written until last time's list has been read: the window opening empty must not wipe it.
    private static bool _started;

    /// <summary>
    /// Writes down what's open now. Called whenever tabs open, close, move or change. Windows that are closing
    /// don't count, except the last one: what it had open is exactly what to bring back.
    /// </summary>
    public static void Remember()
    {
        if (!_started || _restoring || !AppServices.Settings.Current.ReopenLastSession)
        {
            return;
        }

        var windows = WindowManager.All.Where(w => !w.IsClosing).ToList();
        if (windows.Count == 0)
        {
            return; // the last window is closing: keep what it had
        }

        var files = windows.SelectMany(w => w.Documents).Select(d => d.FilePath).Where(IsTheirFile).ToList();
        string? front = (WindowManager.Active is { IsClosing: false } active ? active : windows[0]).SelectedDocument?.FilePath;
        AppServices.ViewState.SetSession(files, front);
        _ = AppServices.ViewState.SaveAsync();
    }

    /// <summary>For the last window, as it closes: its tabs, whatever happens to them during closing.</summary>
    public static void RememberClosing(MainWindow window)
    {
        if (!_started || !AppServices.Settings.Current.ReopenLastSession || WindowManager.All.Any(w => w != window && !w.IsClosing))
        {
            return; // other windows stay open, and they'll say what's open
        }

        AppServices.ViewState.SetSession(window.Documents.Select(d => d.FilePath).Where(IsTheirFile), window.SelectedDocument?.FilePath);
    }

    /// <summary>Opens last time's PDFs in <paramref name="window"/>. Files that have gone are skipped without a word.</summary>
    public static async Task RestoreAsync(MainWindow window)
    {
        var (files, front) = AppServices.ViewState.Session;
        _started = true;
        var there = AppServices.Settings.Current.ReopenLastSession ? files.Where(File.Exists).ToList() : [];
        _restoring = true;
        try
        {
            if (there.Count > 0)
            {
                Log.Info($"Reopening {there.Count} PDF(s) from last time");
                await window.OpenFilesAsync(there);
                if (front != null)
                {
                    window.ShowIfOpen(Path.GetFullPath(front));
                }
            }
        }
        finally
        {
            _restoring = false;
            Remember();
        }
    }

    /// <summary>The person's own files: not the private copies Papery makes (repaired files, attachments, temp).</summary>
    private static bool IsTheirFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        // Only Papery's own folders: other files in Temp are people's too (an attachment opened from Outlook, a download).
        string[] ours = [Windows.Storage.ApplicationData.Current.LocalFolder.Path, Windows.Storage.ApplicationData.Current.TemporaryFolder.Path, Path.Combine(Path.GetTempPath(), "Papery")];
        return !ours.Any(folder => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
