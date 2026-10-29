using Papery.Core.Printing;
using Papery.Pdf;

namespace Papery.Services.Printing;

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape,
}

/// <summary>Everything chosen in the Print dialog.</summary>
internal sealed record PrintJob(
    string Printer,
    byte[] DevMode,
    IReadOnlyList<int> Pages,
    int Copies,
    bool Collate,
    int PagesPerSheet,
    bool Booklet,
    PrintScaling Scaling,
    double CustomPercent,
    PrintOrientation Orientation,
    bool Grayscale);

/// <summary>
/// Prints through classic Windows GDI: works with every printer driver and fully offline.
/// PDFium draws each page straight onto the printer as vectors (sharp text at any DPI).
/// </summary>
internal static class PrintService
{
    /// <summary>Paper orientation that suits the job when set to Auto.</summary>
    public static bool UseLandscapePaper(PdfDocument document, PrintJob job)
    {
        if (job.Booklet)
        {
            return true;
        }

        if (job.Orientation != PrintOrientation.Auto)
        {
            return job.Orientation == PrintOrientation.Landscape;
        }

        var first = document.PageSizes[job.Pages.Count > 0 ? job.Pages[0] : 0];
        return PrintLayout.PreferLandscapePaper(job.PagesPerSheet, first.Width > first.Height);
    }

    /// <returns>Number of sheet sides printed.</returns>
    public static Task<int> PrintAsync(PdfDocument document, string title, PrintJob job, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            bool landscape = UseLandscapePaper(document, job);
            var devMode = Win32Printing.ApplySettings(
                job.DevMode, landscape, (short)Math.Clamp(job.Copies, 1, 999), job.Collate, !job.Grayscale,
                job.Booklet ? Win32Printing.Duplex.ShortEdge : null);

            var sheets = PrintLayout.Impose(job.Pages, job.Booklet ? 2 : job.PagesPerSheet, job.Booklet, landscape);
            nint hdc = Win32Printing.CreatePrinterDC(job.Printer, devMode);
            if (hdc == 0)
            {
                throw new InvalidOperationException("Papery couldn't connect to this printer.");
            }

            bool started = false;
            try
            {
                if (!Win32Printing.StartDocument(hdc, title))
                {
                    // Also returned when the user cancels "Microsoft Print to PDF"'s Save As box.
                    throw new OperationCanceledException();
                }

                started = true;
                int areaW = Win32Printing.GetDeviceCaps(hdc, Win32Printing.HORZRES);
                int areaH = Win32Printing.GetDeviceCaps(hdc, Win32Printing.VERTRES);
                double dpiX = Win32Printing.GetDeviceCaps(hdc, Win32Printing.LOGPIXELSX);
                double dpiY = Win32Printing.GetDeviceCaps(hdc, Win32Printing.LOGPIXELSY);
                double gap = job.PagesPerSheet > 1 || job.Booklet ? dpiX * 0.08 : 0; // ~2 mm between pages

                for (int s = 0; s < sheets.Count; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Win32Printing.StartPage(hdc) <= 0)
                    {
                        throw new IOException("The printer didn't accept a new page.");
                    }

                    foreach (var placed in sheets[s].Pages.Where(p => p.PageIndex >= 0))
                    {
                        double cellX = (placed.X * areaW) + (gap / 2), cellY = (placed.Y * areaH) + (gap / 2);
                        double cellW = (placed.Width * areaW) - gap, cellH = (placed.Height * areaH) - gap;
                        var size = document.PageSizes[placed.PageIndex];
                        var (x, y, w, h, turns) = PrintLayout.FitPage(
                            size.Width / 72.0 * dpiX, size.Height / 72.0 * dpiY, cellW, cellH,
                            job.Scaling, job.CustomPercent, autoRotate: true);
                        await document.PrintPageAsync(
                            hdc, placed.PageIndex,
                            (int)Math.Round(cellX + x), (int)Math.Round(cellY + y), (int)Math.Round(w), (int)Math.Round(h),
                            turns, job.Grayscale, cancellationToken).ConfigureAwait(false);
                    }

                    if (Win32Printing.EndPage(hdc) <= 0)
                    {
                        throw new IOException("The printer stopped accepting pages.");
                    }

                    progress?.Report((s + 1, sheets.Count));
                }

                started = false;
                if (Win32Printing.EndDoc(hdc) <= 0)
                {
                    throw new IOException("The printer couldn't finish the job.");
                }
                return sheets.Count;
            }
            finally
            {
                if (started)
                {
                    _ = Win32Printing.AbortDoc(hdc);
                }

                Win32Printing.DeleteDC(hdc);
            }
        }, cancellationToken);
    }
}
