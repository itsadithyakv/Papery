using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Papery.Services;

/// <summary>
/// Windows file pickers. In a desktop (WinUI 3) app every picker must be told which window
/// owns it, or it fails to appear; these helpers do that in one place.
/// </summary>
internal static class Pickers
{
    public static async Task<IReadOnlyList<string>> PickPdfsAsync(nint hwnd, bool multiple = true)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, hwnd);
        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(f => f.Path).ToArray() ?? [];
        }

        var file = await picker.PickSingleFileAsync();
        return file == null ? [] : [file.Path];
    }

    /// <summary>Asks where to save a PDF. Returns null if cancelled.</summary>
    /// <param name="suggestedFolderOf">A file whose folder the picker should start in, if it can.</param>
    public static async Task<string?> PickSavePdfAsync(nint hwnd, string suggestedName, string? suggestedFolderOf = null)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        InitializeWithWindow.Initialize(picker, hwnd);
        if (suggestedFolderOf != null && File.Exists(suggestedFolderOf))
        {
            try
            {
                // Starts next to the original instead of in Documents.
                picker.SuggestedSaveFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(suggestedFolderOf);
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                Log.Info($"Save picker can't start next to the original: {ex.GetType().Name}");
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            }
        }
        else
        {
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        }

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return null;
        }

        // The picker creates an empty placeholder file; the safe save replaces it.
        return file.Path;
    }

    /// <summary>Asks where to save any other kind of file. Returns null if cancelled.</summary>
    public static async Task<string?> PickSaveAsync(nint hwnd, string suggestedName, string kindName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add(kindName, [extension]);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// The save picker leaves an empty file behind; if saving then fails, remove it so no
    /// 0-byte "PDF" is left lying around. Never touches a file with content.
    /// </summary>
    public static void RemoveIfEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    public static async Task<string?> PickFolderAsync(nint hwnd)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
