using System.Text.Json;
using Windows.Storage;

namespace Papery.Services;

/// <summary>A copy of unsaved work left behind by a crash.</summary>
internal sealed record RecoveredFile(string CopyPath, string InfoPath, string OriginalPath);

/// <summary>
/// Keeps a copy of unsaved work in the app's private folder. A clean close deletes it, so anything
/// still here at the next start means Papery didn't get to finish.
/// </summary>
internal static class RecoveryService
{
    private static string Folder => Directory.CreateDirectory(Path.Combine(ApplicationData.Current.LocalFolder.Path, "recovery")).FullName;

    public static string NewCopyPath() => Path.Combine(Folder, $"{Guid.NewGuid():N}.pdf");

    /// <summary>Records which file a copy belongs to, so it can be offered back by name.</summary>
    public static void Describe(string copyPath, string originalPath)
    {
        File.WriteAllText(Path.ChangeExtension(copyPath, ".json"), JsonSerializer.Serialize(originalPath));
    }

    public static void Forget(string? copyPath)
    {
        if (copyPath == null)
        {
            return;
        }

        TryDelete(copyPath);
        TryDelete(Path.ChangeExtension(copyPath, ".json"));
    }

    /// <summary>What's waiting from last time. Copies with no record, or damaged records, aren't anything anyone can use.</summary>
    public static IReadOnlyList<RecoveredFile> FindLeftovers()
    {
        var found = new List<RecoveredFile>();
        foreach (string info in Directory.GetFiles(Folder, "*.json"))
        {
            string copy = Path.ChangeExtension(info, ".pdf");
            try
            {
                if (File.Exists(copy) && JsonSerializer.Deserialize<string>(File.ReadAllText(info)) is { } original)
                {
                    found.Add(new RecoveredFile(copy, info, original));
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Info($"Unreadable recovery record: {ex.GetType().Name}");
            }

            Forget(copy);
        }

        return found;
    }

    /// <summary>Gives a recovered copy a name people recognise, next to the app's other private files, ready to open.</summary>
    public static string Restore(RecoveredFile file)
    {
        string name = $"{Path.GetFileNameWithoutExtension(file.OriginalPath)} (recovered).pdf";
        string target = Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, "restored", Guid.NewGuid().ToString("N")[..8])).FullName, name);
        File.Move(file.CopyPath, target);
        TryDelete(file.InfoPath);
        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't delete a recovery file: {ex.GetType().Name}");
        }
    }
}
