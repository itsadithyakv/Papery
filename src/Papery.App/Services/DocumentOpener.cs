using Microsoft.UI.Xaml;
using Papery.Pdf;

namespace Papery.Services;

/// <summary>Opens a PDF, asking for a password if needed and explaining clearly when a file can't be opened.</summary>
internal static class DocumentOpener
{
    /// <returns>The open document, or null if the user cancelled or the file can't be opened (already explained).</returns>
    public static async Task<PdfDocument?> OpenAsync(string path, XamlRoot root)
    {
        string name = Path.GetFileName(path);
        string? password = null;
        bool wrong = false;
        while (true)
        {
            try
            {
                var document = await PdfEngine.Shared.OpenAsync(path, password);
                if (document.PageCount == 0)
                {
                    await document.DisposeAsync();
                    await Dialogs.ShowMessageAsync(root, "This PDF has no pages", $"\"{name}\" opened, but there are no pages in it to show.");
                    return null;
                }

                return document;
            }
            catch (PdfOpenException ex) when (ex.Error == PdfOpenError.PasswordRequired)
            {
                password = await Dialogs.AskPasswordAsync(root, name, wrong);
                if (password == null)
                {
                    return null;
                }

                wrong = true;
            }
            catch (PdfOpenException ex) when (ex.Error == PdfOpenError.Damaged)
            {
                if (await RepairAsync(path) is not { } repaired)
                {
                    Log.Info($"Open failed: {ex.Error}");
                    await Dialogs.ShowMessageAsync(root, "Papery can't open this file", Explain(ex.Error, name), Controls.MascotMood.Oops);
                    return null;
                }

                Log.Info("Opened a repaired copy of a damaged file");
                await Dialogs.ShowMessageAsync(root, "This file was damaged", $"Papery rebuilt what it could of \"{name}\" into a new copy, and that's what's open now. Check it looks right, then use Save a copy to keep it. Your original hasn't been changed.", Controls.MascotMood.Oops);
                return repaired;
            }
            catch (PdfOpenException ex)
            {
                Log.Info($"Open failed: {ex.Error}");
                await Dialogs.ShowMessageAsync(root, "Papery can't open this file", Explain(ex.Error, name), Controls.MascotMood.Oops);
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Open failed unexpectedly", ex);
                await Dialogs.ShowMessageAsync(root, "Papery can't open this file", Explain(PdfOpenError.Unknown, name), Controls.MascotMood.Oops);
                return null;
            }
        }
    }

    /// <summary>Tries to rebuild a damaged file into a private copy. Null means it really can't be read.</summary>
    private static async Task<PdfDocument?> RepairAsync(string path)
    {
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "repaired", Guid.NewGuid().ToString("N")[..8])).FullName;
            string output = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)} (repaired).pdf");
            await File.WriteAllBytesAsync(output, []);
            await PdfEngine.Shared.RepairAsync(path, null, output);
            return await PdfEngine.Shared.OpenAsync(output);
        }
        catch (Exception ex) when (ex is Papery.Pdf.Editing.PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"Repair failed: {ex.GetType().Name}");
            return null;
        }
    }

    public static string Explain(PdfOpenError error, string name) => error switch
    {
        PdfOpenError.FileUnreadable =>
            $"\"{name}\" couldn't be read. It may have been moved or deleted, be open in another app that locks it, " +
            "or still be downloading from OneDrive or a network drive. Check the file is available and try again.",
        PdfOpenError.Damaged =>
            $"\"{name}\" isn't a PDF that Papery can read. It may be damaged or only partly downloaded, " +
            "or it may be a different kind of file that has been given a .pdf name. If someone sent it to you, ask them to send it again.",
        PdfOpenError.UnsupportedSecurity =>
            $"\"{name}\" uses a kind of protection that Papery can't open, often a company rights-management system. " +
            "Ask the sender for an unprotected copy.",
        PdfOpenError.TooLarge =>
            $"\"{name}\" is larger than 4 GB, which Papery can't open.",
        _ =>
            $"Something unexpected went wrong while opening \"{name}\". The file has not been changed.",
    };
}
