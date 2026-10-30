using Papery.Pdf.Native;

namespace Papery.Pdf;

public sealed partial class PdfDocument
{
    // Anything smaller is a rule, a bullet or a speck rather than a picture anyone wants back.
    private const int SmallestPictureSide = 16;

    /// <summary>
    /// The pictures on a page, in BGRA with their original pixels, however small they're shown. A picture's
    /// transparency isn't included, and pictures tucked inside reusable page fragments (form XObjects)
    /// aren't reached, so this can miss some that a page shows.
    /// </summary>
    public unsafe Task<IReadOnlyList<PdfBitmap>> GetPageImagesAsync(int pageIndex, PdfWorkPriority priority = PdfWorkPriority.Background, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<PdfBitmap>>(() => WithPage(pageIndex, page =>
        {
            var found = new List<PdfBitmap>();
            int count = PdfiumNative.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
                if (obj == 0 || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                {
                    continue;
                }

                nint bitmap = PdfiumNative.FPDFImageObj_GetBitmap(obj);
                if (bitmap == 0)
                {
                    continue;
                }

                try
                {
                    if (ToBgra(bitmap) is { Width: >= SmallestPictureSide, Height: >= SmallestPictureSide } picture)
                    {
                        found.Add(picture);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            return (IReadOnlyList<PdfBitmap>)found;
        }), cancellationToken, priority);
    }

    /// <summary>Pictures on a page and where they sit (for Word export, which keeps them in reading order).</summary>
    public unsafe Task<IReadOnlyList<(PdfBitmap Picture, NormalizedRect Area)>> GetPlacedPicturesAsync(int pageIndex, PdfWorkPriority priority = PdfWorkPriority.Background, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<(PdfBitmap, NormalizedRect)>>(() => WithPage(pageIndex, page =>
        {
            var found = new List<(PdfBitmap, NormalizedRect)>();
            int count = PdfiumNative.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
                if (obj == 0 || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                {
                    continue;
                }

                float l, b, r, t;
                nint bitmap = PdfiumNative.FPDFImageObj_GetBitmap(obj);
                if (bitmap == 0 || PdfiumNative.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0)
                {
                    if (bitmap != 0)
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }

                    continue;
                }

                try
                {
                    if (ToBgra(bitmap) is { Width: >= SmallestPictureSide, Height: >= SmallestPictureSide } picture)
                    {
                        found.Add((picture, Normalize(page, l, t, r, b)));
                    }
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            return (IReadOnlyList<(PdfBitmap, NormalizedRect)>)found;
        }), cancellationToken, priority);
    }

    internal static unsafe PdfBitmap? ToBgra(nint bitmap)
    {
        int width = PdfiumNative.FPDFBitmap_GetWidth(bitmap), height = PdfiumNative.FPDFBitmap_GetHeight(bitmap);
        int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
        byte* source = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
        int format = PdfiumNative.FPDFBitmap_GetFormat(bitmap);
        if (source == null || width <= 0 || height <= 0)
        {
            return null;
        }

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            byte* row = source + (y * stride);
            for (int x = 0; x < width; x++)
            {
                int to = ((y * width) + x) * 4;
                switch (format)
                {
                    case PdfiumNative.FPDFBitmap_Gray:
                        pixels[to] = pixels[to + 1] = pixels[to + 2] = row[x];
                        pixels[to + 3] = 255;
                        break;
                    case PdfiumNative.FPDFBitmap_BGR:
                        pixels[to] = row[x * 3];
                        pixels[to + 1] = row[(x * 3) + 1];
                        pixels[to + 2] = row[(x * 3) + 2];
                        pixels[to + 3] = 255;
                        break;
                    default: // BGRx and BGRA
                        pixels[to] = row[x * 4];
                        pixels[to + 1] = row[(x * 4) + 1];
                        pixels[to + 2] = row[(x * 4) + 2];
                        pixels[to + 3] = format == PdfiumNative.FPDFBitmap_BGRA ? row[(x * 4) + 3] : (byte)255;
                        break;
                }
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }
}
