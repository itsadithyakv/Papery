using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Papery.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Papery.Views.Convert;

/// <summary>
/// The camera's live picture, so a page can be lined up before taking it. WinUI 3 has no ready-made camera
/// view, so frames are read one at a time and only the newest is shown, which can't fall behind on a slow camera.
/// </summary>
internal sealed class CameraPreview : IAsyncDisposable
{
    private readonly MediaCapture _camera = new();
    private readonly SoftwareBitmapSource _source = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private MediaFrameReader? _reader;
    private SoftwareBitmap? _waiting;
    private bool _drawing;
    private bool _closed;

    private CameraPreview(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Starts the camera. The image then shows it. Returns null when no camera can be used, after logging why.</summary>
    public static async Task<CameraPreview?> StartAsync(Image image)
    {
        var preview = new CameraPreview(image.DispatcherQueue);
        try
        {
            await preview._camera.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });

            var sources = preview._camera.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToArray();
            var source = sources.FirstOrDefault(s => s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview) ?? sources.FirstOrDefault();
            if (source == null)
            {
                await preview.DisposeAsync();
                return null;
            }

            image.Source = preview._source;
            preview._reader = await preview._camera.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            preview._reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            preview._reader.FrameArrived += preview.OnFrame;
            if (await preview._reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                await preview.DisposeAsync();
                return null;
            }

            return preview;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Info($"Camera preview wouldn't start: {ex.GetType().Name}");
            await preview.DisposeAsync();
            return null;
        }
    }

    /// <summary>Takes a full photo, or null if the camera refuses.</summary>
    public async Task<string?> TakePhotoAsync()
    {
        var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("scan-photo.jpg", CreationCollisionOption.GenerateUniqueName);
        try
        {
            await _camera.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), file);
            return file.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Info($"Camera wouldn't take a photo: {ex.GetType().Name}");
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            return null;
        }
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap)
        {
            return;
        }

        // SoftwareBitmapSource only takes premultiplied BGRA.
        var ready = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        Interlocked.Exchange(ref _waiting, ready)?.Dispose();
        _dispatcher.TryEnqueue(() => Guard.Run(DrawNewestAsync, "Showing the camera"));
    }

    private async Task DrawNewestAsync()
    {
        if (_drawing)
        {
            return;
        }

        _drawing = true;
        try
        {
            while (!_closed && Interlocked.Exchange(ref _waiting, null) is { } bitmap)
            {
                using (bitmap)
                {
                    await _source.SetBitmapAsync(bitmap);
                }
            }
        }
        finally
        {
            _drawing = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        if (_reader != null)
        {
            _reader.FrameArrived -= OnFrame;
            try
            {
                await _reader.StopAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Log.Info($"Camera wouldn't stop cleanly: {ex.GetType().Name}");
            }

            _reader.Dispose();
        }

        Interlocked.Exchange(ref _waiting, null)?.Dispose();
        _camera.Dispose();
    }
}
