using System.Globalization;

namespace Papery.Services;

/// <summary>
/// A tiny local diagnostics log (logs\papery.log in Papery's data folder). It never leaves the
/// PC: no telemetry. Kept under 1 MB; the previous log is kept as papery.old.log.
/// File names of documents are not logged, only what went wrong.
/// </summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialize(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "papery.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxSize)
            {
                File.Move(_path, Path.Combine(folder, "papery.old.log"), overwrite: true);
            }
        }
        catch (IOException)
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        if (_path == null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}
