namespace Papery.Pdf.Editing;

/// <summary>
/// Saving that never damages the file being replaced.
/// </summary>
/// <remarks>
/// The new file is written next to the target under a hidden temporary name, read back
/// and checked, and only then swapped in with one rename (Windows ReplaceFile). If anything
/// fails before the swap, the original is untouched; the swap itself is atomic on NTFS.
/// </remarks>
internal static class SafeFile
{
    private const string TempSuffix = ".papery-tmp";

    /// <summary>A hidden file in the target's folder (same volume, so the final rename is atomic).</summary>
    public static string CreateTemp(string targetPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        string temp = Path.Combine(folder, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            using (File.Create(temp, 1, FileOptions.None))
            {
            }

            File.SetAttributes(temp, FileAttributes.Hidden);
            return temp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEditException(PdfEditError.CannotWrite, targetPath, ex);
        }
    }

    /// <summary>Moves a checked temp file over the target (or into place when there is none).</summary>
    public static void Commit(string tempPath, string targetPath)
    {
        try
        {
            File.SetAttributes(tempPath, FileAttributes.Normal);
            if (File.Exists(targetPath))
            {
                // ReplaceFile keeps the original's ACLs and creation time. The backup lets
                // Windows roll back if the swap fails half way; it is removed afterwards.
                string backup = tempPath + ".bak";
                File.Replace(tempPath, targetPath, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            TryDelete(tempPath);
            throw new PdfEditException(PdfEditError.FileInUse, targetPath, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new PdfEditException(PdfEditError.CannotWrite, targetPath, ex);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray hidden temp file is harmless.
        }
    }

    /// <summary>Picks "name (2).pdf", "name (3).pdf"... when <paramref name="path"/> is taken.</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string folder = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(folder, $"{name} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33), ERROR_UNABLE_TO_REMOVE_REPLACED (1175)
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1175;
}
