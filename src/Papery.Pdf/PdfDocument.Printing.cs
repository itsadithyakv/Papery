using Papery.Pdf.Native;

namespace Papery.Pdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Draws a page onto a printer device context (Windows GDI), as vectors where possible.
    /// <paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/> are in
    /// printer pixels; <paramref name="rotate"/> is 0-3 quarter turns clockwise.
    /// The caller owns the device context and must keep it alive until the task completes.
    /// </summary>
    public Task PrintPageAsync(
        nint deviceContext,
        int pageIndex,
        int x,
        int y,
        int width,
        int height,
        int rotate,
        bool grayscale,
        CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        int flags = PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_PRINTING | (grayscale ? PdfiumNative.FPDF_GRAYSCALE : 0);
        return Run(() => WithPage(pageIndex, page =>
            PdfiumNative.FPDF_RenderPage(deviceContext, page, x, y, width, height, rotate, flags)),
            cancellationToken, PdfWorkPriority.Background);
    }
}
