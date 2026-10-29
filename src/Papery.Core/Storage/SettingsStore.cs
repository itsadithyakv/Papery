namespace Papery.Core.Storage;

/// <summary>Loads and saves <see cref="AppSettings"/>, and tells listeners when they change.</summary>
public sealed class SettingsStore
{
    private readonly JsonFileStore<AppSettings> _file;

    public SettingsStore(string path)
    {
        _file = new JsonFileStore<AppSettings>(path, StateJsonContext.Default.AppSettings);
        Current = _file.Load();
    }

    public event EventHandler? Changed;

    public AppSettings Current { get; }

    /// <summary>Applies a change, notifies listeners and saves in the background.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = _file.SaveAsync(Current);
    }
}
