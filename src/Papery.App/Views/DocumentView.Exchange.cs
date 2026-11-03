using Microsoft.UI.Xaml;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Convert;

namespace Papery.Views;

/// <summary>Swapping comments and form answers with other apps, and saving as PowerPoint.</summary>
public sealed partial class DocumentView
{
    // ================================================================= comments to and from other apps

    private void OnExportComments(object sender, RoutedEventArgs e) => Guard.Run(ExportCommentsAsync, "Saving comments");

    /// <summary>Includes markup not saved yet, so a reviewer gets what's on screen.</summary>
    private async Task ExportCommentsAsync()
    {
        if (_document is not { } document
            || await Pickers.PickSaveAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath) + " comments", "Comments for Acrobat and other apps", ".xfdf") is not { } output)
        {
            return;
        }

        int count = await PdfEngine.Shared.ExportCommentsAsync(document, output);
        if (count == 0)
        {
            Pickers.RemoveIfEmpty(output);
            Toaster.Show("This PDF has no comments to save", "");
            return;
        }

        Toaster.Show(count == 1 ? "Saved 1 comment" : $"Saved {count:N0} comments", "", "Show", () => Shell.ShowInFolder(output));
    }

    private void OnImportComments(object sender, RoutedEventArgs e) => Guard.Run(ImportCommentsAsync, "Bringing in comments");

    private async Task ImportCommentsAsync()
    {
        if (_document == null || XamlRoot == null || !await ProDialogs.RequireProAsync(XamlRoot, Tool("draw"))
            || await Pickers.PickFileAsync(GetWindowHandle?.Invoke() ?? 0, ".xfdf") is not { } source)
        {
            return;
        }

        CommentImportResult? result = null;
        try
        {
            await ChangeFileAsync("Comments brought in", "", async input => result = await PdfEngine.Shared.ImportCommentsAsync(input, source, FilePath));
        }
        catch (InvalidDataException)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "That isn't a comments file", "Papery reads comments saved as XFDF, the format Acrobat and most PDF apps use for exporting comments.");
            return;
        }

        if (result is { Skipped: > 0 } or { Added: 0 })
        {
            ShowNotice(
                result.Added == 0 ? "No new comments" : $"{result.Added:N0} comments brought in",
                result.Skipped > 0 ? $"{result.Skipped:N0} couldn't come across, such as sounds, attached files or review ticks." : "Every comment in that file is already here.");
        }
    }

    // ================================================================= form answers to and from a file

    private void OnExportAnswers(object sender, RoutedEventArgs e) => Guard.Run(ExportAnswersAsync, "Saving the answers");

    /// <summary>A spreadsheet for people, or XFDF for other PDF apps: the kind chosen in the save box decides.</summary>
    private async Task ExportAnswersAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        await FlushFormAsync();
        string name = Path.GetFileNameWithoutExtension(FilePath) + " answers";
        if (await Pickers.PickSaveAsync(GetWindowHandle?.Invoke() ?? 0, name, [("Spreadsheet (CSV)", ".csv"), ("Form data for PDF apps (XFDF)", ".xfdf")]) is not { } output)
        {
            return;
        }

        var format = string.Equals(Path.GetExtension(output), ".xfdf", StringComparison.OrdinalIgnoreCase) ? FormDataFormat.Xfdf : FormDataFormat.Csv;
        int count = await PdfEngine.Shared.ExportFormDataAsync(document, output, format);
        if (count == 0)
        {
            Pickers.RemoveIfEmpty(output);
            Toaster.Show("This PDF has no form fields", "");
            return;
        }

        Toaster.Show(count == 1 ? "Saved 1 answer" : $"Saved {count:N0} answers", "", "Show", () => Shell.ShowInFolder(output));
    }

    private void OnImportAnswers(object sender, RoutedEventArgs e) => Guard.Run(ImportAnswersAsync, "Filling from a file");

    private async Task ImportAnswersAsync()
    {
        if (_document == null || XamlRoot == null || await Pickers.PickFileAsync(GetWindowHandle?.Invoke() ?? 0, ".csv", ".xfdf") is not { } source)
        {
            return;
        }

        FormImportResult? result = null;
        try
        {
            await ChangeFileAsync("Form filled in", "", async input => result = await PdfEngine.Shared.ImportFormDataAsync(input, source, FilePath));
        }
        catch (InvalidDataException)
        {
            await Dialogs.ShowMessageAsync(XamlRoot, "That file can't fill this form", "Papery reads answers saved as CSV (a field name and a value on each line) or as XFDF from a PDF app.");
            return;
        }

        if (result is { Unknown: > 0 } or { Skipped: > 0 } or { Filled: 0 })
        {
            ShowNotice(
                result.Filled == 0 ? "Nothing was filled in" : $"{result.Filled:N0} answers filled in",
                $"{result.Unknown + result.Skipped:N0} didn't match a field on this form, or the field doesn't take that answer.");
        }
    }

    // ================================================================= PowerPoint

    private void OnExportPowerPoint(object sender, RoutedEventArgs e) => Guard.Run(ExportPowerPointAsync, "Saving as PowerPoint");

    private async Task ExportPowerPointAsync()
    {
        if (_document is not { } document || !await AllowConvertAsync("pdf-to-powerpoint")
            || await Pickers.PickSaveAsync(GetWindowHandle?.Invoke() ?? 0, Path.GetFileNameWithoutExtension(FilePath), "PowerPoint presentation", ".pptx") is not { } output)
        {
            return;
        }

        bool done = await BusyDialog.RunAsync(XamlRoot, "Making slides", (progress, cancel) => PdfEngine.Shared.ExportPowerPointAsync(document, output, null, progress, cancel));
        if (done)
        {
            Toaster.Show("Saved. Text on each slide can be edited", "", "Show", () => Shell.ShowInFolder(output));
        }
        else
        {
            Pickers.RemoveIfEmpty(output);
        }
    }
}
