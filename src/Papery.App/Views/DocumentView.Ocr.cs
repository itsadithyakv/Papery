using Microsoft.UI.Xaml;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Convert;
using Windows.Globalization;

namespace Papery.Views;

/// <summary>Text recognition: reading one page on the spot, or making a whole scan searchable for good.</summary>
public sealed partial class DocumentView
{
    private Language? _ocrLanguage;
    private bool _readingPage;

    private void OnReadPage(object sender, RoutedEventArgs e) => Guard.Run(ReadThisPageAsync, "Reading the page");

    /// <summary>Lets words in pictures on this page be selected and copied. The file isn't touched.</summary>
    private async Task ReadThisPageAsync()
    {
        if (_document is not { } document || _readingPage || !await AllowConvertAsync("ocr"))
        {
            return;
        }

        var language = _ocrLanguage ?? OcrService.DefaultLanguage;
        if (language == null)
        {
            await OcrDialog.ShowAsync(XamlRoot, document.PageCount, null); // explains how to add a language
            return;
        }

        _readingPage = true;
        int page = Viewer.CurrentPage;
        try
        {
            Toaster.Show("Reading this page", "");
            var read = await OcrService.ReadPageAsync(document, page, language);
            if (_document != document)
            {
                return;
            }

            if (read.Words.Count == 0)
            {
                Toaster.Show("No new words found on this page", "");
                return;
            }

            await document.AddLiveTextAsync(read);
            Toaster.Show("Ready. Drag across the words to select them", "");
        }
        finally
        {
            _readingPage = false;
        }
    }

    private void OnMakeSearchable(object sender, RoutedEventArgs e) => Guard.Run(MakeSearchableAsync, "Making it searchable");

    /// <summary>Reads the chosen pages and adds what it finds to the file as an invisible layer.</summary>
    private async Task MakeSearchableAsync()
    {
        if (_document is not { } document || !await AllowConvertAsync("ocr")
            || await OcrDialog.ShowAsync(XamlRoot, document.PageCount, _ocrLanguage) is not { } choice)
        {
            return;
        }

        _ocrLanguage = choice.Language;
        var found = new List<OcrPage>();
        bool finished = await BusyDialog.RunAsync(XamlRoot, "Reading your pages", async (progress, cancel) =>
        {
            for (int i = 0; i < choice.Pages.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var read = await OcrService.ReadPageAsync(document, choice.Pages[i], choice.Language, cancel);
                if (read.Words.Count > 0)
                {
                    found.Add(read);
                }

                progress.Report((double)(i + 1) / choice.Pages.Count);
            }
        });

        if (!finished)
        {
            return;
        }

        if (found.Count == 0)
        {
            Toaster.Show("No new words found. These pages may already be searchable", "");
            return;
        }

        string done = found.Count == 1 ? "1 page is now searchable" : $"{found.Count:N0} pages are now searchable";
        await ChangeFileAsync(done, "", input => PdfEngine.Shared.AddTextLayerAsync(input, found, FilePath));
    }
}
