using Papery.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Papery.Services;

/// <summary>Reading pictures for putting on pages. Windows decodes them, so HEIC and WEBP work where their codecs are installed.</summary>
internal static class ImageFiles
{
    public static readonly string[] Types = [".png", ".jpg", ".jpeg", ".bmp", ".heic", ".webp", ".tif", ".tiff"];

    public static bool IsImage(string path) => Types.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> PickAsync(nint hwnd)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (string type in Types)
        {
            picker.FileTypeFilter.Add(type);
        }

        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Loads a picture no larger than <paramref name="maxSide"/>. Returns null when it can't be read.</summary>
    public static async Task<PdfBitmap?> LoadAsync(string path, int maxSide)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double scale = Math.Min(1, (double)maxSide / Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
            uint width = Math.Max(1, (uint)(decoder.OrientedPixelWidth * scale)), height = Math.Max(1, (uint)(decoder.OrientedPixelHeight * scale));
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return new PdfBitmap((int)width, (int)height, (int)width * 4, data.DetachPixelData());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"Couldn't read a picture: {ex.GetType().Name}");
            return null;
        }
    }
}
