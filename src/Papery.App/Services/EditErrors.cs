using Microsoft.UI.Xaml;
using Papery.Pdf;
using Papery.Pdf.Editing;

namespace Papery.Services;

/// <summary>Turns save problems into calm, specific messages. Every one says the original is safe, because it is.</summary>
internal static class EditErrors
{
    /// <summary>Shows the right message for an exception from a save. Returns false if it wasn't a known save problem.</summary>
    public static async Task<bool> ShowAsync(Exception ex, XamlRoot root)
    {
        (string title, string message) = ex switch
        {
            PdfEditException e => Explain(e),
            PdfOpenException e => ("Papery can't read one of the files", DocumentOpener.Explain(e.Error, Path.GetFileName(e.FilePath))),
            _ => (string.Empty, string.Empty),
        };
        if (title.Length == 0)
        {
            Log.Error("Save failed unexpectedly", ex);
            (title, message) = ("Something went wrong", "Papery couldn't finish saving. Your original file has not been changed.");
        }

        await Dialogs.ShowMessageAsync(root, title, message, Controls.MascotMood.Oops);
        return true;
    }

    public static (string Title, string Message) Explain(PdfEditException e)
    {
        string name = Path.GetFileName(e.FilePath);
        return e.Error switch
        {
            PdfEditError.NotAllowed => (
                "This PDF is protected from changes",
                $"Whoever made \"{name}\" switched off changes to its pages. Papery respects that. " +
                "If you're the author, open it with the owner password to make changes."),
            PdfEditError.FileInUse => (
                "The file is open in another app",
                $"Another app is using \"{name}\", so it can't be replaced right now. Close it there and try again, " +
                "or use Save as copy. Nothing has been changed."),
            PdfEditError.CannotWrite => (
                "Papery can't save there",
                $"\"{name}\" couldn't be written. The folder may be read-only, full, or no longer available. " +
                "Try saving a copy somewhere else. Your original file has not been changed."),
            PdfEditError.VerifyFailed => (
                "The result didn't pass Papery's check",
                "Papery reads every file back before saving it, and this one didn't read back correctly, so it was thrown away. " +
                "Your original file has not been changed."),
            _ => (
                "Papery couldn't build the new PDF",
                $"A page in \"{name}\" is too damaged to copy. Your original file has not been changed."),
        };
    }
}
