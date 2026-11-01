namespace Papery.Services;

/// <summary>Papery's working copies of people's documents, and getting rid of them when something went wrong.</summary>
internal static class TempFiles
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Papery");

    public static string AttachmentsFolder => Path.Combine(Root, "attachments");

    /// <summary>
    /// After a crash nothing gets to tidy up, and these can be undo copies or unlocked files. Only what's a day old goes,
    /// as a test copy of Papery running alongside would share the folder.
    /// </summary>
    public static void SweepOld()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string kind in Directory.GetDirectories(Root))
            {
                foreach (string entry in Directory.GetFileSystemEntries(kind))
                {
                    if (File.GetLastWriteTimeUtc(entry) < cutoff)
                    {
                        Remove(entry);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't tidy up old working files: {ex.GetType().Name}");
        }
    }

    /// <summary>Attachments opened to look at. Every tab is closed by the time this runs.</summary>
    public static void ClearAttachments()
    {
        if (Directory.Exists(AttachmentsFolder))
        {
            Remove(AttachmentsFolder);
        }
    }

    private static void Remove(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Couldn't delete an old working file: {ex.GetType().Name}");
        }
    }
}
