using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Papery.Controls;
using Papery.Core.Imaging;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Windows.Devices.Enumeration;
using Windows.Devices.Scanners;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Papery.Views.Convert;

/// <summary>
/// Scan to PDF: a page comes from a scanner, the camera or a photo; its edges are found and can be
/// dragged; it's squared up and cleaned; and the pages kept become one PDF.
/// </summary>
internal static class ScanDialog
{
    private const int MaxSide = 3000;

    public static async Task<string?> ShowAsync(XamlRoot root, nint hwnd)
    {
        var kept = new List<PdfBitmap>();
        PdfBitmap? current = null;

        var sources = new PillBar();
        sources.AddItem("Scanner", "", "scanner");
        sources.AddItem("Camera", "", "camera");
        sources.AddItem("Photo", "", "photo");
        sources.SelectedIndex = 0;

        var get = new Button { Content = "Get a page", Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(sources);
        top.Children.Add(get);

        var cropper = new CornerCropper { Width = 500, Height = 340 };
        var live = new Image { Width = 500, Height = 340, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        CameraPreview? preview = null;
        var looks = new PillBar();
        looks.AddItem("Colour", null, "colour");
        looks.AddItem("Grey", null, "grey");
        looks.AddItem("Black & white", null, "bw", "Black and white");
        looks.SelectedIndex = 0;
        var keep = new Button { Content = "Keep this page", IsEnabled = false, Style = (Style)Application.Current.Resources["PaperySecondaryButtonStyle"] };
        var bottom = new Grid { ColumnSpacing = 12 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(looks);
        Grid.SetColumn(status, 1);
        bottom.Children.Add(status);
        Grid.SetColumn(keep, 2);
        bottom.Children.Add(keep);

        var panel = new StackPanel { Spacing = 12, Width = 500 };
        panel.Children.Add(top);
        panel.Children.Add(cropper);
        panel.Children.Add(live);
        panel.Children.Add(bottom);

        var dialog = new ContentDialog
        {
            Title = "Scan to PDF",
            Content = panel,
            PrimaryButtonText = "Make PDF",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 620.0;

        void Say(string words) => status.Text = words;

        async Task StopCameraAsync()
        {
            if (preview is { } running)
            {
                preview = null;
                await running.DisposeAsync();
            }

            live.Source = null;
            live.Visibility = Visibility.Collapsed;
            cropper.Visibility = Visibility.Visible;
        }

        async Task ShowCameraAsync()
        {
            if (preview == null)
            {
                preview = await CameraPreview.StartAsync(live);
                if (preview == null)
                {
                    Say("Papery can't use the camera. Check Settings > Privacy & security > Camera.");
                    return;
                }
            }

            cropper.Visibility = Visibility.Collapsed;
            live.Visibility = Visibility.Visible;
            get.Content = "Take the picture";
            Say("Line up the page, then take the picture.");
        }

        sources.SelectionChanged += (_, _) => Guard.Run(async () =>
        {
            get.Content = "Get a page";
            await StopCameraAsync();
            if (sources.SelectedIndex == 1)
            {
                await ShowCameraAsync();
            }
        }, "Using the camera");

        async Task ShowPageAsync(PdfBitmap? page)
        {
            if (page == null)
            {
                return; // whatever went wrong has already been said
            }

            current = page;
            keep.IsEnabled = true;
            var corners = await Task.Run(() => DocumentScan.FindCorners(page.Pixels, page.Width, page.Height, page.Stride));
            cropper.Show(page, corners ?? ScanCorners.WholePicture);
            Say(corners == null ? "Drag the corners to the page's edges." : "Edges found. Drag a corner to adjust.");
        }

        get.Click += (_, _) => Guard.Run(async () =>
        {
            get.IsEnabled = false;
            try
            {
                switch (sources.SelectedIndex)
                {
                    case 1:
                        if (live.Visibility != Visibility.Visible || preview == null)
                        {
                            await ShowCameraAsync(); // back to the live picture for another page
                            break;
                        }

                        Say("Taking the picture…");
                        if (await preview.TakePhotoAsync() is not { } shot)
                        {
                            Say("The camera didn't take the picture. Try again.");
                            break;
                        }

                        try
                        {
                            var taken = await ImageFiles.LoadAsync(shot, MaxSide);
                            if (taken == null)
                            {
                                Say("Papery couldn't read the camera's picture.");
                                break;
                            }

                            live.Visibility = Visibility.Collapsed;
                            cropper.Visibility = Visibility.Visible;
                            get.Content = "Another picture";
                            await ShowPageAsync(taken);
                        }
                        finally
                        {
                            File.Delete(shot);
                        }

                        break;
                    case 2:
                        if (await ImageFiles.PickAsync(hwnd) is { } path)
                        {
                            var photo = await ImageFiles.LoadAsync(path, MaxSide);
                            if (photo == null)
                            {
                                Say("Papery couldn't read that picture.");
                            }

                            await ShowPageAsync(photo);
                        }

                        break;
                    default:
                        Say("Scanning…");
                        await ShowPageAsync(await FromScannerAsync(Say));
                        break;
                }
            }
            finally
            {
                get.IsEnabled = true;
            }
        }, "Getting a page");

        keep.Click += (_, _) => Guard.Run(async () =>
        {
            if (current is not { } page)
            {
                return;
            }

            var corners = cropper.Corners;
            var look = (ScanLook)Math.Max(0, looks.SelectedIndex);
            kept.Add(await Task.Run(() =>
            {
                var (pixels, width, height) = DocumentScan.Straighten(page.Pixels, page.Width, page.Height, page.Stride, corners, MaxSide);
                return new PdfBitmap(width, height, width * 4, DocumentScan.Enhance(pixels, width, height, width * 4, look));
            }));
            current = null;
            keep.IsEnabled = false;
            cropper.Clear();
            dialog.IsPrimaryButtonEnabled = true;
            Say(kept.Count == 1 ? "1 page kept. Get another, or make the PDF." : $"{kept.Count:N0} pages kept. Get another, or make the PDF.");
        }, "Keeping a page");

        var answer = await Dialogs.ShowAsync(dialog, root);
        await StopCameraAsync();
        if (answer != ContentDialogResult.Primary || kept.Count == 0)
        {
            return null;
        }

        if (await Pickers.PickSavePdfAsync(hwnd, "Scan") is not { } output)
        {
            return null;
        }

        // A4 at 200 dpi or so is what a scanner gives: treat pixels as 1/200 inch unless the page is tiny.
        var pages = kept.Select(page => new PdfImagePage(page.Width * 72.0 / 200, page.Height * 72.0 / 200, () => Task.FromResult(new PdfImageData(null, page)))).ToArray();
        try
        {
            await PdfEngine.Shared.ImagesToPdfAsync(pages, new ImagePageOptions(ImagePaperSize.A4), output);
            return output;
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await EditErrors.ShowAsync(ex, root);
            return null;
        }
    }

    private static async Task<PdfBitmap?> FromScannerAsync(Action<string> say)
    {
        var devices = await DeviceInformation.FindAllAsync(ImageScanner.GetDeviceSelector());
        if (devices.Count == 0)
        {
            say("No scanner was found. Is it on and connected?");
            return null;
        }

        var scanner = await ImageScanner.FromIdAsync(devices[0].Id);
        var source = scanner.IsScanSourceSupported(ImageScannerScanSource.Flatbed) ? ImageScannerScanSource.Flatbed
            : scanner.IsScanSourceSupported(ImageScannerScanSource.Feeder) ? ImageScannerScanSource.Feeder
            : ImageScannerScanSource.AutoConfigured;
        var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("scans", CreationCollisionOption.OpenIfExists);
        var result = await scanner.ScanFilesToFolderAsync(source, folder);
        if (result.ScannedFiles.Count == 0)
        {
            say("The scanner didn't send a page.");
            return null;
        }

        var file = result.ScannedFiles[0];
        var page = await ImageFiles.LoadAsync(file.Path, MaxSide);
        await file.DeleteAsync(StorageDeleteOption.PermanentDelete); // it's someone's document: don't leave a copy behind
        if (page == null)
        {
            say("Papery couldn't read the scanned page.");
        }

        return page;
    }

}

/// <summary>A picture with four corner handles to drag onto the edges of the page in it.</summary>
internal sealed partial class CornerCropper : Grid
{
    private const double HandleSize = 16;

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Polygon _outline = new()
    {
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x25, 0xE5, 0x53, 0x3D)),
        IsHitTestVisible = false,
    };

    private readonly Ellipse[] _handles = new Ellipse[4];
    private readonly ScanPoint[] _corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
    private int _dragging = -1;
    private double _shownWidth;
    private double _shownHeight;
    private double _offsetX;
    private double _offsetY;
    private PdfBitmap? _picture;

    public CornerCropper()
    {
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        CornerRadius = new CornerRadius(8);
        Children.Add(_image);
        Children.Add(_canvas);
        _canvas.Children.Add(_outline);
        string[] names = ["Top left corner", "Top right corner", "Bottom right corner", "Bottom left corner"];
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            var handle = new Ellipse
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle, names[i]);
            handle.PointerPressed += (_, e) =>
            {
                _dragging = index;
                _canvas.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            _handles[i] = handle;
            _canvas.Children.Add(handle);
        }

        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += (_, _) =>
        {
            _dragging = -1;
            _canvas.ReleasePointerCaptures();
        };
        SizeChanged += (_, _) => Lay();
    }

    public ScanCorners Corners => new(_corners[0], _corners[1], _corners[2], _corners[3]);

    public void Show(PdfBitmap picture, ScanCorners corners)
    {
        _picture = picture;
        _image.Source = PageBitmaps.ToWriteableBitmap(picture);
        _corners[0] = corners.TopLeft;
        _corners[1] = corners.TopRight;
        _corners[2] = corners.BottomRight;
        _corners[3] = corners.BottomLeft;
        foreach (var handle in _handles)
        {
            handle.Visibility = Visibility.Visible;
        }

        Lay();
    }

    public void Clear()
    {
        _picture = null;
        _image.Source = null;
        _outline.Points.Clear();
        foreach (var handle in _handles)
        {
            handle.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging < 0 || _shownWidth <= 0)
        {
            return;
        }

        var at = e.GetCurrentPoint(_canvas).Position;
        _corners[_dragging] = new ScanPoint(Math.Clamp((at.X - _offsetX) / _shownWidth, 0, 1), Math.Clamp((at.Y - _offsetY) / _shownHeight, 0, 1));
        Lay();
    }

    /// <summary>The picture is fitted and centred, so handles are placed against where it actually shows.</summary>
    private void Lay()
    {
        if (_picture is not { } picture || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double scale = Math.Min(ActualWidth / picture.Width, ActualHeight / picture.Height);
        _shownWidth = picture.Width * scale;
        _shownHeight = picture.Height * scale;
        _offsetX = (ActualWidth - _shownWidth) / 2;
        _offsetY = (ActualHeight - _shownHeight) / 2;
        _outline.Points.Clear();
        for (int i = 0; i < 4; i++)
        {
            double x = _offsetX + (_corners[i].X * _shownWidth), y = _offsetY + (_corners[i].Y * _shownHeight);
            _outline.Points.Add(new Windows.Foundation.Point(x, y));
            Canvas.SetLeft(_handles[i], x - (HandleSize / 2));
            Canvas.SetTop(_handles[i], y - (HandleSize / 2));
        }
    }
}
