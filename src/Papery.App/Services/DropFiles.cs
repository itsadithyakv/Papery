using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Papery.Services;

/// <summary>Gets file paths out of a drag-and-drop from File Explorer, the desktop or Outlook.</summary>
internal static class DropFiles
{
    public static async Task<IReadOnlyList<string>> GetPathsAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            return items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading dropped files failed", ex);
            return [];
        }
        finally
        {
            deferral.Complete();
        }
    }

    public static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
