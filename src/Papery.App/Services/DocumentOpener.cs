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
            catch (PdfOpenException ex)
            {
                Log.Info($"Open failed: {ex.Error}");
                await Dialogs.ShowMessageAsync(root, "Papery can't open this file", Explain(ex.Error, name));
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Open failed unexpectedly", ex);
                await Dialogs.ShowMessageAsync(root, "Papery can't open this file", Explain(PdfOpenError.Unknown, name));
                return null;
            }
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
