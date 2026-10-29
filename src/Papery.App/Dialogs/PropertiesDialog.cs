using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;
using Papery.Services;

namespace Papery.Views;

/// <summary>File > Properties: title, author, page size, pages, file size, PDF version, protection.</summary>
internal static class PropertiesDialog
{
    public static async Task ShowAsync(XamlRoot root, PdfDocument document)
    {
        var meta = await document.GetMetadataAsync();
        var first = document.PageSizes[0];
        bool mixed = document.PageSizes.Any(s => Math.Abs(s.Width - first.Width) > 1 || Math.Abs(s.Height - first.Height) > 1);

        var rows = new List<(string Label, string? Value)>
        {
            ("File", Path.GetFileName(document.FilePath)),
            ("Location", Path.GetDirectoryName(document.FilePath)),
            ("Title", meta.Title),
            ("Author", meta.Author),
            ("Subject", meta.Subject),
            ("Keywords", meta.Keywords),
            ("Pages", document.PageCount.ToString("N0", CultureInfo.CurrentCulture)),
            ("Page size", PageSizeText(first) + (mixed ? " (pages vary)" : string.Empty)),
            ("File size", FileSizeText(document.FileSize)),
            ("PDF version", document.FileVersion > 0 ? $"{document.FileVersion / 10}.{document.FileVersion % 10}" : null),
            ("Created", meta.Created?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            ("Modified", meta.Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            ("Created with", meta.Creator),
            ("PDF producer", meta.Producer),
            ("Protection", Protection(document)),
        };

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MaxWidth = 520 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (label, value) in rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"] };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }

        await Dialogs.ShowAsync(
            new ContentDialog { Title = "Document properties", Content = new ScrollViewer { Content = grid, MaxHeight = 480 }, CloseButtonText = "Close" },
            root);
    }

    public static string FileSizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>"A4 (210 × 297 mm)" or "8.5 × 11 in (Letter)" depending on the user's region.</summary>
    public static string PageSizeText(PdfPageSize size)
    {
        double wmm = size.Width / 72 * 25.4, hmm = size.Height / 72 * 25.4;
        string? name = KnownSize(Math.Min(wmm, hmm), Math.Max(wmm, hmm));
        bool metric = RegionInfo.CurrentRegion.IsMetric;
        string dims = metric
            ? $"{wmm:0} × {hmm:0} mm"
            : $"{size.Width / 72:0.##} × {size.Height / 72:0.##} in";
        return name == null ? dims : $"{name} ({dims})";
    }

    private static string? KnownSize(double shortMm, double longMm)
    {
        (string Name, double W, double H)[] known =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
        ];
        return known.FirstOrDefault(k => Math.Abs(k.W - shortMm) < 2 && Math.Abs(k.H - longMm) < 2).Name;
    }

    private static string? Protection(PdfDocument document)
    {
        if (!document.IsEncrypted)
        {
            return "None";
        }

        var limits = new List<string>();
        if (!document.CanPrint)
        {
            limits.Add("printing");
        }

        if (!document.CanCopy)
        {
            limits.Add("copying text");
        }

        string scheme = document.SecurityRevision switch { >= 5 => "AES-256", 4 => "AES-128 or RC4", _ => "RC4 (older)" };
        return limits.Count == 0 ? $"Encrypted ({scheme})" : $"Encrypted ({scheme}); the author doesn't allow {string.Join(" or ", limits)}";
    }
}
