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

    // A long side like this is 16 inches at 300 dpi: more than any page needs, and it keeps memory sane.
    private const int MaxPageSide = 5000;

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

    public static async Task<IReadOnlyList<string>> PickManyAsync(nint hwnd)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (string type in Types)
        {
            picker.FileTypeFilter.Add(type);
        }

        InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToArray() ?? [];
    }

    public static async Task<string?> PickSaveTextAsync(nint hwnd, string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Text", [".txt"]);
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>A picture's size on a page, from its pixels and resolution, and whether its JPEG bytes can go in untouched.</summary>
    public static async Task<(double WidthPoints, double HeightPoints, bool KeepsJpeg)?> MeasureAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double dpiX = decoder.DpiX >= 30 ? decoder.DpiX : 96, dpiY = decoder.DpiY >= 30 ? decoder.DpiY : 96;

            // A JPEG turned by its EXIF tag would come out sideways if its bytes were used as they are.
            bool upright = decoder.OrientedPixelWidth == decoder.PixelWidth && decoder.OrientedPixelHeight == decoder.PixelHeight;
            bool jpeg = decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId && upright;
            return (decoder.OrientedPixelWidth * 72 / dpiX, decoder.OrientedPixelHeight * 72 / dpiY, jpeg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"Couldn't read a picture: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>A picture's pixels for a PDF page: the original JPEG bytes when possible.</summary>
    public static async Task<Papery.Pdf.Editing.PdfImageData> LoadForPageAsync(string path, bool keepsJpeg)
    {
        if (keepsJpeg)
        {
            return new Papery.Pdf.Editing.PdfImageData(await File.ReadAllBytesAsync(path), null);
        }

        return new Papery.Pdf.Editing.PdfImageData(null, await LoadAsync(path, MaxPageSide));
    }

    public static byte[] Crop(PdfBitmap picture, int left, int top, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(picture.Pixels, ((top + y) * picture.Stride) + (left * 4), pixels, y * width * 4, width * 4);
        }

        return pixels;
    }

    public static async Task<byte[]> EncodeAsync(PdfBitmap picture, bool jpeg, double dpi)
    {
        using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, (uint)picture.Width, (uint)picture.Height, dpi, dpi, picture.Stride == picture.Width * 4 ? picture.Pixels : Packed(picture));
        await encoder.FlushAsync();
        byte[] bytes = new byte[memory.Size];
        memory.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(memory);
        await reader.LoadAsync((uint)memory.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Encodes and writes a picture. The kind comes from the path's extension.</summary>
    public static async Task SaveAsync(PdfBitmap picture, string path, double dpi)
    {
        bool jpeg = Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase);
        await File.WriteAllBytesAsync(path, await EncodeAsync(picture, jpeg, dpi));
    }

    private static byte[] Packed(PdfBitmap picture) => Crop(picture, 0, 0, picture.Width, picture.Height);

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
