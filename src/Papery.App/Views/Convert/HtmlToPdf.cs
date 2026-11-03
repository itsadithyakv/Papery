using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Papery.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Papery.Views.Convert;

/// <summary>
/// Turns a saved web page or HTML file into a PDF with the browser engine built into Windows (WebView2), the same way
/// Edge prints. Papery stays offline: scripts are off, and anything the page asks for that isn't a file on this PC
/// (pictures from the web, fonts, trackers) is refused, so a page saved for reading prints as it was saved.
/// </summary>
internal static class HtmlToPdf
{
    private static readonly string[] Kinds = [".html", ".htm", ".mht", ".mhtml", ".xhtml", ".svg"];

    /// <summary>Asks for the page if none was given, then where to save. Returns the new PDF, or null.</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, nint window, IReadOnlyList<string> given)
    {
        string? source = given.FirstOrDefault(f => Kinds.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (source == null)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
            foreach (string kind in Kinds)
            {
                picker.FileTypeFilter.Add(kind);
            }

            InitializeWithWindow.Initialize(picker, window);
            source = (await picker.PickSingleFileAsync())?.Path;
        }

        // Not started beside the page: the picker would take the page's own name, ".html" and all.
        if (source == null || await Pickers.PickSavePdfAsync(window, Path.GetFileNameWithoutExtension(source)) is not { } output)
        {
            return null;
        }

        string temp = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "html")).FullName, $"{Guid.NewGuid():N}.pdf");
        string? problem = await PrintAsync(root, source, temp);
        if (problem != null)
        {
            Pickers.RemoveIfEmpty(output);
            await Dialogs.ShowMessageAsync(root, "Couldn't make the PDF", problem);
            return null;
        }

        try
        {
            File.Copy(temp, output, overwrite: true);
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(output);
            await Dialogs.ShowMessageAsync(root, "Couldn't save the PDF", "Choose another folder, or check there's room on the drive.");
            return null;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Loads the page out of sight inside a small "working" dialog and prints it. Returns null, or what went wrong.</summary>
    private static async Task<string?> PrintAsync(XamlRoot root, string source, string output)
    {
        // The browser has to be in the window to start, but its print layout doesn't depend on its size.
        var web = new WebView2 { Width = 1, Height = 1, Opacity = 0 };
        var panel = new StackPanel { Spacing = 14, Width = 320 };
        panel.Children.Add(new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = Path.GetFileName(source), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(web);
        var dialog = new ContentDialog { Title = "Making a PDF", Content = panel, CloseButtonText = "Cancel" };

        string? problem = "Cancelled";
        dialog.Opened += async (_, _) =>
        {
            try
            {
                problem = await LoadAndPrintAsync(web, source, output);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("HTML to PDF failed", ex);
                problem = "Windows' web engine (Microsoft Edge WebView2) isn't working on this PC. Installing or repairing it from Microsoft fixes this.";
            }

            dialog.Hide();
        };

        await Dialogs.ShowAsync(dialog, root);
        web.Close();
        return problem == "Cancelled" ? "Stopped before it finished." : problem;
    }

    private static async Task<string?> LoadAndPrintAsync(WebView2 web, string source, string output)
    {
        await web.EnsureCoreWebView2Async();
        var core = web.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // Only this PC's files and pictures stored inside the page. Nothing goes to the internet.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
        };

        var loaded = new TaskCompletionSource<bool>();
        core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
        core.Navigate(new Uri(source).AbsoluteUri);
        if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(60))) != loaded.Task || !loaded.Task.Result)
        {
            return "The page didn't open. It may be damaged, or not a web page.";
        }

        var settings = core.Environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.4;

        // Letter where people use it, A4 everywhere else.
        bool letter = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE";
        settings.PageWidth = letter ? 8.5 : 8.27;
        settings.PageHeight = letter ? 11 : 11.69;
        return await core.PrintToPdfAsync(output, settings) ? null : "The page couldn't be turned into a PDF.";
    }
}
