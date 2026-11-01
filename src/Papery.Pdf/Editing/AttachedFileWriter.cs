namespace Papery.Pdf.Editing;

/// <summary>Saves a file that came out of a PDF, marked the way a browser marks a download.</summary>
public static class AttachedFileWriter
{
    /// <summary>The mark makes Windows warn before running it, because a PDF from anywhere can carry a program.</summary>
    public static async Task WriteAsync(string path, byte[] data)
    {
        await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Some drives (FAT32, some network shares) can't hold the mark. The file itself is saved.
        }
    }
}
