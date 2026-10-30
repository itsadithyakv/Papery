using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

public enum ImagePaperSize
{
    /// <summary>Each page is the size of its picture.</summary>
    MatchImage,
    A4,
    Letter,
    Legal,
}

public enum ImageOrientation
{
    /// <summary>Each page turns to suit its picture.</summary>
    Auto,
    Portrait,
    Landscape,
}

public sealed record ImagePageOptions(ImagePaperSize Paper = ImagePaperSize.MatchImage, ImageOrientation Orientation = ImageOrientation.Auto, double Margin = 0);

/// <summary>A picture's pixels: original JPEG bytes when they can be kept as they are, otherwise a bitmap.</summary>
public sealed record PdfImageData(byte[]? Jpeg, PdfBitmap? Bitmap);

/// <summary>One picture to become a page. It's loaded only when its turn comes, so long lists don't fill memory.</summary>
public sealed record PdfImagePage(double WidthPoints, double HeightPoints, Func<Task<PdfImageData>> Load);

/// <summary>Where a picture goes on its page, in points from the bottom-left.</summary>
public readonly record struct ImagePlacement(double PageWidth, double PageHeight, double X, double Y, double Width, double Height);

/// <summary>The sums for laying pictures on pages, kept apart so they can be tested without any PDF.</summary>
public static class ImagePageLayout
{
    public static ImagePlacement Place(double imageWidth, double imageHeight, ImagePageOptions options)
    {
        double margin = Math.Max(0, options.Margin);
        if (options.Paper == ImagePaperSize.MatchImage)
        {
            return new ImagePlacement(imageWidth + (2 * margin), imageHeight + (2 * margin), margin, margin, imageWidth, imageHeight);
        }

        var (shortSide, longSide) = options.Paper switch
        {
            ImagePaperSize.Letter => (612.0, 792.0),
            ImagePaperSize.Legal => (612.0, 1008.0),
            _ => (595.28, 841.89),
        };
        bool landscape = options.Orientation == ImageOrientation.Landscape
            || (options.Orientation == ImageOrientation.Auto && imageWidth > imageHeight);
        double pageWidth = landscape ? longSide : shortSide, pageHeight = landscape ? shortSide : longSide;

        // Fits inside the margins, whole, and in the middle. Margins too big for the page are ignored rather than failing.
        double roomWidth = pageWidth - (2 * margin), roomHeight = pageHeight - (2 * margin);
        if (roomWidth <= 0 || roomHeight <= 0)
        {
            roomWidth = pageWidth;
            roomHeight = pageHeight;
        }

        double scale = Math.Min(roomWidth / imageWidth, roomHeight / imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        return new ImagePlacement(pageWidth, pageHeight, (pageWidth - width) / 2, (pageHeight - height) / 2, width, height);
    }
}

/// <summary>Builds a new PDF from pictures, through the checked save.</summary>
internal static class ImageToPdfWriter
{
    public static async Task WriteAsync(PdfiumThread thread, IReadOnlyList<PdfImagePage> pages, ImagePageOptions options, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (pages.Count == 0)
        {
            throw new ArgumentException("There are no pictures to add.", nameof(pages));
        }

        outputPath = Path.GetFullPath(outputPath);
        RawPdf? doc = null;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await thread.InvokeAsync(() => doc = RawPdf.CreateNew(), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pages[i];
                var data = await page.Load().ConfigureAwait(false);
                int index = i;
                await thread.InvokeAsync(() => AddPage(doc!, index, data, ImagePageLayout.Place(page.WidthPoints, page.HeightPoints, options), outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                progress?.Report((double)(i + 1) / pages.Count);
            }

            await thread.InvokeAsync(() => PdfComposer.Save(doc!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, null, pages.Count, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            SafeFile.TryDelete(temp);
        }
    }

    private static int AddPage(RawPdf doc, int index, PdfImageData data, ImagePlacement place, string outputPath)
    {
        nint page = PdfiumNative.FPDFPage_New(doc.Handle, index, place.PageWidth, place.PageHeight);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
        }

        try
        {
            nint image = PdfiumNative.FPDFPageObj_NewImageObj(doc.Handle);
            if (image == 0 || !SetPixels(image, data))
            {
                if (image != 0)
                {
                    PdfiumNative.FPDFPageObj_Destroy(image);
                }

                throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
            }

            _ = PdfiumNative.FPDFImageObj_SetMatrix(image, place.Width, 0, 0, place.Height, place.X, place.Y);
            PdfiumNative.FPDFPage_InsertObject(page, image);
            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }

        return index;
    }

    internal static unsafe bool SetPixels(nint image, PdfImageData data)
    {
        // Original JPEG bytes go in untouched: no quality lost, and the file stays about the size of the photos.
        if (data.Jpeg is { } jpeg)
        {
            fixed (byte* bytes = jpeg)
            {
                var access = new PdfiumNative.FPDF_FILEACCESS { m_FileLen = (uint)jpeg.Length, m_GetBlock = &ReadBlock, m_Param = (nint)bytes };
                nint noPages = 0;
                return PdfiumNative.FPDFImageObj_LoadJpegFileInline(&noPages, 0, image, &access) != 0;
            }
        }

        if (data.Bitmap is not { } picture)
        {
            return false;
        }

        fixed (byte* pixels = picture.Pixels)
        {
            nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(picture.Width, picture.Height, PdfiumNative.FPDFBitmap_BGRA, (nint)pixels, picture.Stride);
            bool ok = bitmap != 0 && PdfiumNative.FPDFImageObj_SetBitmap(null, 0, image, bitmap) != 0;
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
            return ok;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int ReadBlock(nint param, uint position, byte* buffer, uint size)
    {
        Buffer.MemoryCopy((byte*)param + position, buffer, size, size);
        return 1;
    }
}
