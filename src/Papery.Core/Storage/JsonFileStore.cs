using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Papery.Core.Storage;

/// <summary>
/// Keeps one small object in a JSON file. Writes go to a temporary file first and then
/// replace the real one, so a crash or power cut mid-write never leaves a corrupt file.
/// A missing or unreadable file simply gives a fresh default object.
/// </summary>
#pragma warning disable CA1001 // SemaphoreSlim only needs disposing if its WaitHandle is used; ours never is.
public sealed class JsonFileStore<T>
#pragma warning restore CA1001
    where T : class, new()
{
    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonFileStore(string path, JsonTypeInfo<T> typeInfo)
    {
        _path = path;
        _typeInfo = typeInfo;
    }

    public T Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                return JsonSerializer.Deserialize(stream, _typeInfo) ?? new T();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must never stop Papery from starting: start fresh.
        }

        return new T();
    }

    public async Task SaveAsync(T value)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, _typeInfo).ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
