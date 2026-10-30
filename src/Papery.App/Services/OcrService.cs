using System.Runtime.InteropServices.WindowsRuntime;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using OcrWord = Papery.Pdf.Editing.OcrWord;

namespace Papery.Services;

/// <summary>Reads words off pages with the OCR built into Windows. Nothing leaves the computer.</summary>
internal static class OcrService
{
    private const double ReadingDpi = 300;

    public static IReadOnlyList<Language> Languages => OcrEngine.AvailableRecognizerLanguages;

    /// <summary>The language to offer first: the user's own if Windows can read it, otherwise the first it can.</summary>
    public static Language? DefaultLanguage =>
        OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage ?? (Languages.Count > 0 ? Languages[0] : null);

    /// <summary>Opens the Windows page where more OCR languages can be added.</summary>
    public static void OpenLanguageSettings() => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage"));

    /// <summary>The words on a page that aren't already real text, in fractions of the page as displayed.</summary>
    public static async Task<OcrPage> ReadPageAsync(PdfDocument document, int pageIndex, Language language, CancellationToken cancellationToken = default)
    {
        if (OcrEngine.TryCreateFromLanguage(language) is not { } engine)
        {
            return new OcrPage(pageIndex, []);
        }

        var size = document.PageSizes[pageIndex];
        double scale = Math.Min(ReadingDpi / 72, OcrEngine.MaxImageDimension / Math.Max(size.Width, size.Height));
        int width = Math.Max(1, (int)(size.Width * scale)), height = Math.Max(1, (int)(size.Height * scale));
        var page = await document.RenderPageAsync(pageIndex, width, height, PdfRenderOptions.None, PdfWorkPriority.Background, cancellationToken);
        byte[] pixels = page.Stride == page.Width * 4 ? page.Pixels : ImageFiles.Crop(page, 0, 0, page.Width, page.Height);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, page.Width, page.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var box = word.BoundingRect;
                var area = new NormalizedRect(box.X / page.Width, box.Y / page.Height, box.Width / page.Width, box.Height / page.Height);

                // Pages can mix real text with pictures of text. Only the pictures need help.
                if (await document.GetCharIndexAtAsync(pageIndex, area.X + (area.Width / 2), area.Y + (area.Height / 2), cancellationToken) < 0)
                {
                    words.Add(new OcrWord(word.Text, area));
                }
            }
        }

        return new OcrPage(pageIndex, words);
    }
}
