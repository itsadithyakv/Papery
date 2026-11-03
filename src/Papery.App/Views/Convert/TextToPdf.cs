using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Core.Text;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Papery.Views.Convert;

/// <summary>
/// Makes a PDF from a text file or from what was copied: copied text is laid out like a text file, and a copied
/// picture goes to Images to PDF. Text is set in a typewriter font, so columns made with spaces stay lined up.
/// </summary>
internal static class TextToPdf
{
    private const long LargestFile = 20L * 1024 * 1024;

    private static readonly string[] Kinds = [".txt", ".log", ".md", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".srt", ".nfo"];

    public static bool IsTextFile(string path) => Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Uses a text file given, or offers what was copied, or asks for a file. Returns the new PDF, or null.</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, nint window, IReadOnlyList<string> given)
    {
        string? file = given.FirstOrDefault(IsTextFile);
        if (file == null && given.Where(ImageFiles.IsImage).ToList() is { Count: > 0 } pictures)
        {
            return await ImagesToPdfDialog.ShowAsync(root, window, pictures);
        }

        var copied = file == null ? Copied() : null;
        bool? useCopied = copied == null ? false : await OfferCopiedAsync(root);
        if (useCopied == null)
        {
            return null;
        }

        if (copied != null && useCopied == true)
        {
            if (copied.Contains(StandardDataFormats.Text))
            {
                return await MakeAsync(root, window, await copied.GetTextAsync(), "Copied text");
            }

            if (copied.Contains(StandardDataFormats.StorageItems))
            {
                var paths = (await copied.GetStorageItemsAsync()).OfType<StorageFile>().Select(f => f.Path).ToList();
                if (paths.FirstOrDefault(IsTextFile) is { } copiedFile)
                {
                    file = copiedFile;
                }
                else if (paths.Where(ImageFiles.IsImage).ToList() is { Count: > 0 } copiedPictures)
                {
                    return await ImagesToPdfDialog.ShowAsync(root, window, copiedPictures);
                }
            }
            else if (await SavePictureAsync(copied) is { } picture)
            {
                try
                {
                    return await ImagesToPdfDialog.ShowAsync(root, window, [picture]);
                }
                finally
                {
                    File.Delete(picture);
                }
            }
        }

        file ??= await Pickers.PickFileAsync(window, Kinds);
        if (file == null)
        {
            return null;
        }

        string text;
        try
        {
            if (new FileInfo(file).Length > LargestFile)
            {
                await Dialogs.ShowMessageAsync(root, "This file is too big", "Papery makes PDFs from text files up to 20 MB.");
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(file);
            if (bytes.Take(8192).Count(b => b == 0) > 8 && !bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) && !bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            {
                await Dialogs.ShowMessageAsync(root, "This isn't a text file", "It holds more than letters, so it would come out as nonsense. Open it in the app it belongs to and print it to PDF from there.");
                return null;
            }

            text = PlainText.Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(root, "Couldn't read the file", "It may be open in another app, or no longer there.");
            return null;
        }

        return await MakeAsync(root, window, text, Path.GetFileNameWithoutExtension(file));
    }

    /// <summary>What's on the clipboard, when it's something Papery can turn into a PDF.</summary>
    private static DataPackageView? Copied()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) || content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems) ? content : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null; // another app is holding the clipboard
        }
    }

    /// <summary>True for what was copied, false for a file, null for neither.</summary>
    private static async Task<bool?> OfferCopiedAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = "Make a PDF from",
            PrimaryButtonText = "What you copied",
            SecondaryButtonText = "A text file",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await Dialogs.ShowAsync(dialog, root) switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    /// <summary>A copied picture, saved as a PNG in Papery's temp folder for Images to PDF.</summary>
    private static async Task<string?> SavePictureAsync(DataPackageView copied)
    {
        if (!copied.Contains(StandardDataFormats.Bitmap))
        {
            return null;
        }

        using IRandomAccessStreamWithContentType stream = await (await copied.GetBitmapAsync()).OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var folder = await StorageFolder.GetFolderFromPathAsync(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "copied")).FullName);
        var file = await folder.CreateFileAsync("Copied picture.png", CreationCollisionOption.GenerateUniqueName);
        using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }

        return file.Path;
    }

    private static async Task<string?> MakeAsync(XamlRoot root, nint window, string text, string name)
    {
        string tidy = PlainText.Tidy(text);
        if (string.IsNullOrWhiteSpace(tidy.Replace('\f', ' ')))
        {
            await Dialogs.ShowMessageAsync(root, "There's no text to put in a PDF", string.Empty);
            return null;
        }

        if (await Pickers.PickSavePdfAsync(window, name) is not { } output)
        {
            return null;
        }

        var options = new TextPageOptions(HtmlToPdf.UsesLetter ? new PdfPageSize(612, 792) : new PdfPageSize(595.28f, 841.89f));
        try
        {
            if (!await BusyDialog.RunAsync(root, "Making your PDF", (progress, cancel) =>
                PdfEngine.Shared.TextToPdfAsync(PlainText.Wrap(tidy, options.Columns), options, output, progress, cancel)))
            {
                Pickers.RemoveIfEmpty(output);
                return null;
            }
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await EditErrors.ShowAsync(ex, root);
            return null;
        }

        return output;
    }
}
