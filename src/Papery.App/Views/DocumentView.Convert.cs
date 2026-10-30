using System.Text;
using Microsoft.UI.Xaml;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Convert;
using Papery.Views.Fill;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Papery.Views;

/// <summary>Convert mode: pages to pictures, Snapshot, pictures out of a PDF, text out of a PDF, and new PDFs from pictures or scans.</summary>
public sealed partial class DocumentView
{
    private const int ConvertModeIndex = 5;
    private const double SnapshotDpi = 200;

    // The longest side of a page picture. Past this, memory rather than sharpness becomes the limit.
    private const int LongestPictureSide = 9000;

    /// <summary>A new PDF is ready for a tab of its own.</summary>
    internal event EventHandler<string>? NewPdfMade;

    private void EnterConvertMode()
    {
        ConvertToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
        ConvertSnapshot.IsChecked = false;
    }

    private void LeaveConvertMode()
    {
        ConvertToolbar.Visibility = Visibility.Collapsed;
        ConvertSnapshot.IsChecked = false;
    }

    private async Task<bool> AllowConvertAsync(string key) => XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool(key));

    // ================================================================= pages to pictures

    private void OnSavePagesAsImages(object sender, RoutedEventArgs e) => Guard.Run(SavePagesAsImagesAsync, "Saving pictures");

    private async Task SavePagesAsImagesAsync()
    {
        if (_document is not { } document || !await AllowConvertAsync("pdf-to-images")
            || await PagesToImagesDialog.ShowAsync(XamlRoot, document.PageCount) is not { } choice
            || await Pickers.PickFolderAsync(GetWindowHandle?.Invoke() ?? 0) is not { } folder)
        {
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(FilePath);
        string extension = choice.Jpeg ? ".jpg" : ".png";
        int digits = Math.Max(3, document.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        bool done = await BusyDialog.RunAsync(XamlRoot, "Saving your pages", async (progress, cancel) =>
        {
            for (int i = 0; i < choice.Pages.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                int page = choice.Pages[i];
                var (w, h) = PictureSize(document.PageSizes[page], choice.Dpi);
                var bitmap = await document.RenderPageAsync(page, w, h, PdfRenderOptions.Annotations, PdfWorkPriority.Background, cancel);
                string name = $"{stem}-{(page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0')}";
                await ImageFiles.SaveAsync(bitmap, FreeName(folder, name, extension), choice.Dpi);
                progress.Report((double)(i + 1) / choice.Pages.Count);
            }
        });
        if (done)
        {
            string saved = choice.Pages.Count == 1 ? "Page saved" : $"{choice.Pages.Count:N0} pages saved";
            Toaster.Show(saved, "", "Open folder", () => _ = Windows.System.Launcher.LaunchFolderPathAsync(folder));
        }
    }

    private static (int Width, int Height) PictureSize(PdfPageSize page, double dpi)
    {
        double scale = Math.Min(dpi / 72, LongestPictureSide / Math.Max(page.Width, page.Height));
        return (Math.Max(1, (int)(page.Width * scale)), Math.Max(1, (int)(page.Height * scale)));
    }

    private static string FreeName(string folder, string stem, string extension)
    {
        string path = Path.Combine(folder, stem + extension);
        for (int copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(folder, $"{stem} ({copy}){extension}");
        }

        return path;
    }

    // ================================================================= snapshot

    private void OnSnapshot(object sender, RoutedEventArgs e)
    {
        if (ConvertSnapshot.IsChecked != true)
        {
            Viewer.Tool = null;
            return;
        }

        // Snapshot is free, unlike the rest of Convert.
        Toaster.Show("Drag around what you want a picture of", "");
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            Viewer.Tool = null;
            ConvertSnapshot.IsChecked = false;
            Guard.Run(() => TakeSnapshotAsync(page, start, end), "Snapshot");
        });
    }

    private async Task TakeSnapshotAsync(int page, PagePoint start, PagePoint end)
    {
        if (_document is not { } document)
        {
            return;
        }

        var (w, h) = PictureSize(document.PageSizes[page], SnapshotDpi);
        int left = (int)(Math.Min(start.X, end.X) * w), top = (int)(Math.Min(start.Y, end.Y) * h);
        int width = (int)(Math.Abs(end.X - start.X) * w), height = (int)(Math.Abs(end.Y - start.Y) * h);
        if (width < 4 || height < 4)
        {
            Toaster.Show("Drag a box around what you want", "");
            return;
        }

        var whole = await document.RenderPageAsync(page, w, h);
        var part = new PdfBitmap(width, height, width * 4, ImageFiles.Crop(whole, left, top, width, height));
        byte[] png = await ImageFiles.EncodeAsync(part, jpeg: false, 96);
        var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(png));
        memory.Seek(0);
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(memory));
        Clipboard.SetContent(package);
        Toaster.Show("Picture copied", "");
    }

    // ================================================================= pictures out

    private void OnExtractPictures(object sender, RoutedEventArgs e) => Guard.Run(ExtractPicturesAsync, "Saving pictures");

    private async Task ExtractPicturesAsync()
    {
        if (_document is not { } document || !await AllowConvertAsync("pdf-to-images")
            || await Pickers.PickFolderAsync(GetWindowHandle?.Invoke() ?? 0) is not { } folder)
        {
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(FilePath);
        int saved = 0;
        bool done = await BusyDialog.RunAsync(XamlRoot, "Finding pictures", async (progress, cancel) =>
        {
            for (int page = 0; page < document.PageCount; page++)
            {
                cancel.ThrowIfCancellationRequested();
                var pictures = await document.GetPageImagesAsync(page, PdfWorkPriority.Background, cancel);
                for (int i = 0; i < pictures.Count; i++)
                {
                    await ImageFiles.SaveAsync(pictures[i], FreeName(folder, $"{stem}-page{page + 1}-{i + 1}", ".png"), 96);
                    saved++;
                }

                progress.Report((double)(page + 1) / document.PageCount);
            }
        });

        if (done && saved == 0)
        {
            Toaster.Show("No pictures were found in this PDF", "");
        }
        else if (done)
        {
            Toaster.Show(saved == 1 ? "1 picture saved" : $"{saved:N0} pictures saved", "", "Open folder", () => _ = Windows.System.Launcher.LaunchFolderPathAsync(folder));
        }
    }

    // ================================================================= text out

    private void OnSaveText(object sender, RoutedEventArgs e) => Guard.Run(SaveTextAsync, "Saving text");

    private void OnCopyAllText(object sender, RoutedEventArgs e) => Guard.Run(CopyAllTextAsync, "Copying text");

    private async Task<string?> AllTextAsync()
    {
        if (_document is not { } document)
        {
            return null;
        }

        var text = new StringBuilder();
        bool done = await BusyDialog.RunAsync(XamlRoot, "Reading the text", async (progress, cancel) =>
        {
            for (int page = 0; page < document.PageCount; page++)
            {
                cancel.ThrowIfCancellationRequested();
                text.Append(await document.GetPageTextAsync(page, PdfWorkPriority.Background, cancel));
                text.Append("\r\n\r\n");
                progress.Report((double)(page + 1) / document.PageCount);
            }
        });
        if (!done)
        {
            return null;
        }

        string all = text.ToString().Trim();
        if (all.Length == 0)
        {
            Toaster.Show("No text here. A scan is only a picture of words", "");
            return null;
        }

        return all;
    }

    private async Task SaveTextAsync()
    {
        if (!await AllowConvertAsync("pdf-to-images") || await ImageFiles.PickSaveTextAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath)) is not { } path)
        {
            return;
        }

        if (await AllTextAsync() is { } text)
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Toaster.Show("Text saved", "");
        }
        else
        {
            Pickers.RemoveIfEmpty(path);
        }
    }

    private async Task CopyAllTextAsync()
    {
        if (!await AllowConvertAsync("pdf-to-images") || await AllTextAsync() is not { } text)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Toaster.Show("All the text is copied", "");
    }

    // ================================================================= new PDFs

    private void OnImagesToPdf(object sender, RoutedEventArgs e) => Guard.Run(ImagesToPdfAsync, "Making a PDF");

    private async Task ImagesToPdfAsync()
    {
        if (await AllowConvertAsync("images-to-pdf") && await ImagesToPdfDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0) is { } made)
        {
            NewPdfMade?.Invoke(this, made);
        }
    }

    private void OnScan(object sender, RoutedEventArgs e) => Guard.Run(ScanAsync, "Scanning");

    private async Task ScanAsync()
    {
        if (await AllowConvertAsync("scan") && await ScanDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0) is { } made)
        {
            NewPdfMade?.Invoke(this, made);
        }
    }

    private void OnPrintToPdfHelp(object sender, RoutedEventArgs e) => Guard.Run(() => PrintToPdfHelp.ShowAsync(XamlRoot), "Help");
}
