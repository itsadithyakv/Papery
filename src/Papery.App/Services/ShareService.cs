using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Papery.Services;

/// <summary>
/// Hands a file to Windows Share (Mail, Teams, Nearby sharing and the rest). A desktop window has to ask for its
/// own share manager, and each window is wired up only once, so a file waits here until Windows asks for it.
/// </summary>
internal static class ShareService
{
    private static readonly Dictionary<nint, string> Waiting = [];
    private static readonly HashSet<nint> Wired = [];

    public static void Share(nint window, string path)
    {
        var manager = DataTransferManagerInterop.GetForWindow(window);
        if (Wired.Add(window))
        {
            manager.DataRequested += (_, e) => Provide(window, e.Request);
        }

        Waiting[window] = path;
        DataTransferManagerInterop.ShowShareUIForWindow(window);
    }

    private static async void Provide(nint window, DataRequest request)
    {
        var deferral = request.GetDeferral();
        try
        {
            if (Waiting.TryGetValue(window, out string? path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                request.Data.Properties.Title = Path.GetFileName(path);
                request.Data.SetStorageItems([file]);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Windows asks on its own schedule, so a failure here can only be told to the share window itself.
            Log.Error("Sharing failed", ex);
            request.FailWithDisplayText("Papery couldn't share this file.");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
