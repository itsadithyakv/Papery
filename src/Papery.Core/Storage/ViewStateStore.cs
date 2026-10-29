namespace Papery.Core.Storage;

/// <summary>
/// Recent files and "where was I" for every file. Everything stays on this PC.
/// </summary>
/// <remarks>
/// Thread-safe. Changes are kept in memory and written by <see cref="SaveAsync"/>;
/// the app saves after changes with a short delay so scrolling doesn't hammer the disk.
/// </remarks>
public sealed class ViewStateStore
{
    public const int MaxRecentFiles = 20;
    public const int MaxRememberedFiles = 500;

    private readonly JsonFileStore<ViewStateData> _file;
    private readonly Lock _lock = new();
    private ViewStateData _data;

    public ViewStateStore(string path)
    {
        _file = new JsonFileStore<ViewStateData>(path, StateJsonContext.Default.ViewStateData);
        _data = _file.Load();

        // JSON round-trips lose the comparer: restore case-insensitive paths (Windows file names).
        _data.Files = new Dictionary<string, FileViewState>(_data.Files, StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler? RecentFilesChanged;

    public IReadOnlyList<string> RecentFiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _data.RecentFiles];
            }
        }
    }

    public FileViewState? Get(string path)
    {
        lock (_lock)
        {
            return _data.Files.TryGetValue(Normalize(path), out var state)
                ? new FileViewState { Page = state.Page, Zoom = state.Zoom, ViewMode = state.ViewMode, LastOpened = state.LastOpened }
                : null;
        }
    }

    public void Set(string path, int page, double zoom, Viewing.ViewMode? viewMode)
    {
        lock (_lock)
        {
            _data.Files[Normalize(path)] = new FileViewState { Page = page, Zoom = zoom, ViewMode = viewMode, LastOpened = DateTimeOffset.Now };
            if (_data.Files.Count > MaxRememberedFiles)
            {
                // Forget the files opened longest ago.
                foreach (string old in _data.Files.OrderBy(f => f.Value.LastOpened).Take(_data.Files.Count - MaxRememberedFiles).Select(f => f.Key).ToList())
                {
                    _data.Files.Remove(old);
                }
            }
        }
    }

    /// <summary>Moves a file to the top of the recent list.</summary>
    public void AddRecent(string path)
    {
        lock (_lock)
        {
            string normalized = Normalize(path);
            _data.RecentFiles.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
            _data.RecentFiles.Insert(0, normalized);
            if (_data.RecentFiles.Count > MaxRecentFiles)
            {
                _data.RecentFiles.RemoveRange(MaxRecentFiles, _data.RecentFiles.Count - MaxRecentFiles);
            }
        }

        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveRecent(string path)
    {
        lock (_lock)
        {
            _data.RecentFiles.RemoveAll(p => string.Equals(p, Normalize(path), StringComparison.OrdinalIgnoreCase));
        }

        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRecent()
    {
        lock (_lock)
        {
            _data.RecentFiles.Clear();
        }

        RecentFilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveAsync()
    {
        ViewStateData snapshot;
        lock (_lock)
        {
            snapshot = new ViewStateData
            {
                RecentFiles = [.. _data.RecentFiles],
                Files = new Dictionary<string, FileViewState>(_data.Files, StringComparer.OrdinalIgnoreCase),
            };
        }

        return _file.SaveAsync(snapshot);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
