using Papery.Pdf.Native;

namespace Papery.Pdf;

public sealed partial class PdfDocument
{
    /// <summary>Renders a whole page into a BGRA bitmap of exactly <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public Task<PdfBitmap> RenderPageAsync(
        int pageIndex,
        int width,
        int height,
        PdfRenderOptions options = PdfRenderOptions.Annotations,
        PdfWorkPriority priority = PdfWorkPriority.Visible,
        CancellationToken cancellationToken = default) =>
        RenderRegionAsync(pageIndex, width, height, new PixelRect(0, 0, width, height), options, priority, cancellationToken);

    /// <summary>
    /// Renders one rectangle (a "tile") of a page that is notionally <paramref name="pageWidth"/> ×
    /// <paramref name="pageHeight"/> pixels. Used at high zoom, where a whole page bitmap
    /// would be hundreds of megabytes: only the visible tiles are ever drawn.
    /// </summary>
    public unsafe Task<PdfBitmap> RenderRegionAsync(
        int pageIndex,
        int pageWidth,
        int pageHeight,
        PixelRect region,
        PdfRenderOptions options = PdfRenderOptions.Annotations,
        PdfWorkPriority priority = PdfWorkPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Height);
        CheckPageIndex(pageIndex);

        return Run(() => WithPage(pageIndex, page =>
        {
            int stride = region.Width * 4;
            byte[] pixels = new byte[stride * region.Height];
            fixed (byte* p = pixels)
            {
                // Render straight into our managed array: no extra copy.
                nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(region.Width, region.Height, PdfiumNative.FPDFBitmap_BGRA, (nint)p, stride);
                if (bitmap == 0)
                {
                    throw new InvalidOperationException($"Could not create a {region.Width}×{region.Height} bitmap.");
                }

                try
                {
                    _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, region.Width, region.Height, 0xFFFFFFFF);

                    // Offsetting the page by -region.X/-region.Y draws just that part of it into the bitmap.
                    PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, pageWidth, pageHeight, 0, (int)options);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            return new PdfBitmap(region.Width, region.Height, stride, pixels);
        }), cancellationToken, priority);
    }

    /// <summary>
    /// Where the photos/pictures are on a page. Dark reading mode inverts everything
    /// except these areas, so images keep their real colours.
    /// </summary>
    public Task<IReadOnlyList<NormalizedRect>> GetImageAreasAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage<IReadOnlyList<NormalizedRect>>(pageIndex, page =>
        {
            var areas = new List<NormalizedRect>();
            int budget = 20_000; // pages with huge object counts (maps) are only partly scanned
            int count = PdfiumNative.FPDFPage_CountObjects(page);
            for (int i = 0; i < count && budget > 0; i++)
            {
                CollectImages(page, PdfiumNative.FPDFPage_GetObject(page, i), null, areas, depth: 0, ref budget);
            }

            return areas;
        }), cancellationToken, PdfWorkPriority.Visible);
    }

    private static unsafe void CollectImages(
        nint page, nint obj, PdfiumNative.FS_MATRIX? toPage, List<NormalizedRect> areas, int depth, ref int budget)
    {
        budget--;
        if (obj == 0 || depth > 8)
        {
            return;
        }

        int type = PdfiumNative.FPDFPageObj_GetType(obj);
        if (type == PdfiumNative.FPDF_PAGEOBJ_IMAGE)
        {
            float l, b, r, t;
            if (PdfiumNative.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0)
            {
                return;
            }

            // Objects inside a form XObject report bounds in the form's space: map them to the page.
            if (toPage is { } m)
            {
                (double x1, double y1) = Apply(m, l, b);
                (double x2, double y2) = Apply(m, r, t);
                (double x3, double y3) = Apply(m, l, t);
                (double x4, double y4) = Apply(m, r, b);
                l = (float)Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
                r = (float)Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
                b = (float)Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
                t = (float)Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
            }

            areas.Add(Normalize(page, l, t, r, b));
        }
        else if (type == PdfiumNative.FPDF_PAGEOBJ_FORM)
        {
            PdfiumNative.FS_MATRIX formMatrix;
            if (PdfiumNative.FPDFPageObj_GetMatrix(obj, &formMatrix) == 0)
            {
                return;
            }

            var combined = toPage is { } outer ? Multiply(formMatrix, outer) : formMatrix;
            int children = PdfiumNative.FPDFFormObj_CountObjects(obj);
            for (int i = 0; i < children && budget > 0; i++)
            {
                CollectImages(page, PdfiumNative.FPDFFormObj_GetObject(obj, (uint)i), combined, areas, depth + 1, ref budget);
            }
        }
    }

    private static (double X, double Y) Apply(PdfiumNative.FS_MATRIX m, double x, double y) =>
        ((m.A * x) + (m.C * y) + m.E, (m.B * x) + (m.D * y) + m.F);

    /// <summary>Matrix that applies <paramref name="first"/> and then <paramref name="second"/>.</summary>
    private static PdfiumNative.FS_MATRIX Multiply(PdfiumNative.FS_MATRIX first, PdfiumNative.FS_MATRIX second) => new()
    {
        A = (first.A * second.A) + (first.B * second.C),
        B = (first.A * second.B) + (first.B * second.D),
        C = (first.C * second.A) + (first.D * second.C),
        D = (first.C * second.B) + (first.D * second.D),
        E = (first.E * second.A) + (first.F * second.C) + second.E,
        F = (first.E * second.B) + (first.F * second.D) + second.F,
    };
}
