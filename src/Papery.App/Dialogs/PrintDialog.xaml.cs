using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Core.Printing;
using Papery.Pdf;
using Papery.Services.Printing;

namespace Papery.Views;

/// <summary>Print options. When it closes with Print, <see cref="Job"/> holds what to print.</summary>
public sealed partial class PrintDialog : ContentDialog
{
    private readonly PdfDocument _document;
    private readonly int _currentPage;
    private readonly nint _ownerWindow;
    private byte[]? _devMode;

    internal PrintDialog(PdfDocument document, int currentPage, nint ownerWindow)
    {
        InitializeComponent();
        _document = document;
        _currentPage = currentPage;
        _ownerWindow = ownerWindow;

        var printers = Win32Printing.GetPrinters();
        foreach (string printer in printers)
        {
            PrinterBox.Items.Add(printer);
        }

        string? preferred = LastPrinter ?? Win32Printing.GetDefaultPrinter();
        PrinterBox.SelectedItem = preferred != null && printers.Contains(preferred) ? preferred : (printers.Count > 0 ? printers[0] : null);
        IsPrimaryButtonEnabled = printers.Count > 0;
        if (printers.Count == 0)
        {
            Summary.Text = "No printers are installed. Add one in Windows Settings > Bluetooth & devices > Printers & scanners.";
        }

        UpdateSummary();
    }

    internal PrintJob? Job { get; private set; }

    /// <summary>Remembered for this session so the next Print starts on the same printer.</summary>
    private static string? LastPrinter { get; set; }

    private void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        _devMode = PrinterBox.SelectedItem is string printer ? Win32Printing.GetDefaultDevMode(printer) : null;
        PropertiesLink.IsEnabled = _devMode != null;
    }

    private void OnPrinterProperties(object sender, RoutedEventArgs e)
    {
        if (PrinterBox.SelectedItem is string printer && _devMode != null)
        {
            _devMode = Win32Printing.ShowPrinterProperties(_ownerWindow, printer, _devMode) ?? _devMode;
        }
    }

    private void OnAnyChange(object sender, object e) => UpdateSummary();

    private void UpdateSummary()
    {
        if (RangeBox == null || Summary == null)
        {
            return; // still loading
        }

        RangeBox.IsEnabled = PagesChoice.SelectedIndex == 2;
        PerSheetBox.IsEnabled = BookletBox.IsChecked != true;
        CustomScaleBox.Visibility = ScalingBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (!TryGetPages(out var pages, out string? error))
        {
            RangeError.Text = error;
            RangeError.Visibility = Visibility.Visible;
            Summary.Text = string.Empty;
            return;
        }

        RangeError.Visibility = Visibility.Collapsed;
        int perSheet = PrintLayout.PagesPerSheetChoices[Math.Max(0, PerSheetBox.SelectedIndex)];
        int copies = (int)Math.Max(1, double.IsNaN(CopiesBox.Value) ? 1 : CopiesBox.Value);
        string summary;
        if (BookletBox.IsChecked == true)
        {
            int sheets = (pages.Count + 3) / 4;
            summary = $"{pages.Count} pages on {sheets} sheet{(sheets == 1 ? string.Empty : "s")} of paper, printed on both sides";
        }
        else
        {
            int sides = (pages.Count + perSheet - 1) / perSheet;
            summary = $"{pages.Count} page{(pages.Count == 1 ? string.Empty : "s")} on {sides} side{(sides == 1 ? string.Empty : "s")} of paper";
        }

        Summary.Text = copies > 1 ? $"{summary}, {copies} copies." : summary + ".";
    }

    private bool TryGetPages(out IReadOnlyList<int> pages, out string? error)
    {
        error = null;
        switch (PagesChoice.SelectedIndex)
        {
            case 1:
                pages = [_currentPage];
                return true;
            case 2:
                return PrintLayout.TryParseRange(RangeBox.Text, _document.PageCount, out pages, out error);
            default:
                pages = Enumerable.Range(0, _document.PageCount).ToList();
                return true;
        }
    }

    private void OnPrintClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (PrinterBox.SelectedItem is not string printer || _devMode == null)
        {
            Summary.Text = "Papery couldn't read this printer's settings. Choose another printer or check it in Windows Settings.";
            args.Cancel = true;
            return;
        }

        if (!TryGetPages(out var pages, out string? error))
        {
            RangeError.Text = error;
            RangeError.Visibility = Visibility.Visible;
            RangeBox.Focus(FocusState.Programmatic);
            args.Cancel = true;
            return;
        }

        LastPrinter = printer;
        Job = new PrintJob(
            printer,
            _devMode,
            pages,
            Copies: (int)Math.Max(1, double.IsNaN(CopiesBox.Value) ? 1 : CopiesBox.Value),
            Collate: true,
            PagesPerSheet: PrintLayout.PagesPerSheetChoices[Math.Max(0, PerSheetBox.SelectedIndex)],
            Booklet: BookletBox.IsChecked == true,
            Scaling: ScalingBox.SelectedIndex switch
            {
                0 => PrintScaling.Fit,
                1 => PrintScaling.ShrinkOversized,
                2 => PrintScaling.ActualSize,
                _ => PrintScaling.Custom,
            },
            CustomPercent: double.IsNaN(CustomScaleBox.Value) ? 100 : CustomScaleBox.Value,
            Orientation: (PrintOrientation)Math.Max(0, OrientationBox.SelectedIndex),
            Grayscale: GrayscaleSwitch.IsOn);
    }
}
