using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>Page size in PDF points (1 pt = 1/72 inch), already accounting for page rotation.</summary>
public readonly record struct PdfPageSize(float Width, float Height);

/// <summary>BGRA pixels, 4 bytes per pixel, rows <see cref="Stride"/> bytes apart.</summary>
public sealed record PdfBitmap(int Width, int Height, int Stride, byte[] Pixels);

public enum PdfFormType
{
    None = 0,
    AcroForm = 1,
    XfaFull = 2,
    XfaForeground = 3,
}

[Flags]
public enum PdfRenderOptions
{
    None = 0,

    /// <summary>Draw annotations (highlights, notes, stamps...).</summary>
    Annotations = PdfiumNative.FPDF_ANNOT,

    /// <summary>Render as greyscale.</summary>
    Grayscale = PdfiumNative.FPDF_GRAYSCALE,

    /// <summary>Use print appearance (hides "screen only" annotations).</summary>
    Printing = PdfiumNative.FPDF_PRINTING,
}

/// <summary>
/// An open PDF file. All methods are safe to call from any thread: the work is
/// forwarded to the PDFium thread and the result comes back as a Task.
/// </summary>
public sealed class PdfDocument : IAsyncDisposable
{
    private readonly PdfiumThread _thread;
    private readonly PdfFileSource _source;
    private nint _handle;

    private unsafe PdfDocument(PdfiumThread thread, PdfFileSource source, nint handle, string path)
    {
        _thread = thread;
        _source = source;
        _handle = handle;
        FilePath = path;

        // Everything below is cheap: none of it parses page contents.
        int pageCount = PdfiumNative.FPDF_GetPageCount(handle);
        var sizes = new PdfPageSize[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            PdfiumNative.FS_SIZEF size;
            sizes[i] = PdfiumNative.FPDF_GetPageSizeByIndexF(handle, i, &size) != 0
                ? new PdfPageSize(size.Width, size.Height)
                : new PdfPageSize(612, 792); // US Letter fallback for unreadable page dictionaries
        }

        PageSizes = sizes;

        int version;
        FileVersion = PdfiumNative.FPDF_GetFileVersion(handle, &version) != 0 ? version : 0;
        SecurityRevision = PdfiumNative.FPDF_GetSecurityHandlerRevision(handle);
        Permissions = PdfiumNative.FPDF_GetDocPermissions(handle);
        WasRepaired = PdfiumNative.FPDF_DocumentHasValidCrossReferenceTable(handle) == 0;
        FormType = (PdfFormType)PdfiumNative.FPDF_GetFormType(handle);
        HasOutline = PdfiumNative.FPDFBookmark_GetFirstChild(handle, 0) != 0;
    }

    public string FilePath { get; }

    public long FileSize => _source.Length;

    public int PageCount => PageSizes.Count;

    public IReadOnlyList<PdfPageSize> PageSizes { get; }

    /// <summary>PDF version times ten, e.g. 17 for PDF 1.7. 0 if unknown.</summary>
    public int FileVersion { get; }

    /// <summary>Security handler revision (e.g. 6 = AES-256), or -1 when not encrypted.</summary>
    public int SecurityRevision { get; }

    public bool IsEncrypted => SecurityRevision >= 0;

    /// <summary>Raw PDF permission bits (PDF spec table 22).</summary>
    public uint Permissions { get; }

    /// <summary>True when the cross-reference table was broken and PDFium rebuilt it.</summary>
    public bool WasRepaired { get; }

    public PdfFormType FormType { get; }

    public bool HasOutline { get; }

    internal static unsafe Task<PdfDocument> OpenAsync(
        PdfEngine engine, string path, string? password, CancellationToken cancellationToken)
    {
        return engine.Thread.InvokeAsync(() =>
        {
            PdfFileSource source;
            try
            {
                source = new PdfFileSource(path);
            }
            catch (PdfOpenException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PdfOpenException(PdfOpenError.FileUnreadable, path, ex);
            }

            if (source.Length == 0)
            {
                source.Dispose();
                throw new PdfOpenException(PdfOpenError.Damaged, path);
            }

            // PDFium accepts UTF-8 passwords and converts them for older encryption itself.
            byte[] passwordBytes = Encoding.UTF8.GetBytes((password ?? string.Empty) + '\0');
            nint handle;
            fixed (byte* pw = passwordBytes)
            {
                handle = PdfiumNative.FPDF_LoadCustomDocument(source.Access, pw);
            }

            if (handle == 0)
            {
                uint error = PdfiumNative.FPDF_GetLastError();
                source.Dispose();
                throw new PdfOpenException(MapError(error), path);
            }

            return new PdfDocument(engine.Thread, source, handle, path);
        }, cancellationToken);
    }

    /// <summary>Renders one page into a BGRA bitmap of exactly <paramref name="width"/> Ã— <paramref name="height"/> pixels.</summary>
    public unsafe Task<PdfBitmap> RenderPageAsync(
        int pageIndex,
        int width,
        int height,
        PdfRenderOptions flags = PdfRenderOptions.Annotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        CheckPageIndex(pageIndex);

        return _thread.InvokeAsync(() =>
        {
            nint page = LoadPage(pageIndex);
            try
            {
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                fixed (byte* p = pixels)
                {
                    // Render straight into our managed array: no extra copy.
                    nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(width, height, PdfiumNative.FPDFBitmap_BGRA, (nint)p, stride);
                    if (bitmap == 0)
                    {
                        throw new InvalidOperationException($"Could not create a {width}Ã—{height} bitmap.");
                    }

                    try
                    {
                        _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, (int)flags);
                    }
                    finally
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }
                }

                return new PdfBitmap(width, height, stride, pixels);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    /// <summary>Returns all text on a page, in PDFium's reading order.</summary>
    public unsafe Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return _thread.InvokeAsync(() =>
        {
            nint page = LoadPage(pageIndex);
            nint textPage = PdfiumNative.FPDFText_LoadPage(page);
            try
            {
                if (textPage == 0)
                {
                    return string.Empty;
                }

                int count = PdfiumNative.FPDFText_CountChars(textPage);
                if (count <= 0)
                {
                    return string.Empty;
                }

                var buffer = new char[count + 1];
                fixed (char* b = buffer)
                {
                    int written = PdfiumNative.FPDFText_GetText(textPage, 0, count, (ushort*)b);
                    return new string(buffer, 0, Math.Max(0, written - 1)); // drop the terminator
                }
            }
            finally
            {
                if (textPage != 0)
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }

                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    /// <summary>Number of annotations (notes, highlights, links, form widgets...) on a page.</summary>
    public Task<int> GetAnnotationCountAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return _thread.InvokeAsync(() =>
        {
            nint page = LoadPage(pageIndex);
            try
            {
                return PdfiumNative.FPDFPage_GetAnnotCount(page);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle == 0)
        {
            return;
        }

        await _thread.InvokeAsync(() =>
        {
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = 0;
        }).ConfigureAwait(false);

        // Only free the file access struct after PDFium has let go of it.
        _source.Dispose();
    }

    private nint LoadPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        nint page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
        if (page == 0)
        {
            throw new InvalidDataException($"Page {pageIndex + 1} of '{FilePath}' could not be loaded.");
        }

        return page;
    }

    private void CheckPageIndex(int pageIndex) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)pageIndex, (uint)PageCount, nameof(pageIndex));

    private static PdfOpenError MapError(uint error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => PdfOpenError.FileUnreadable,
        PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.Damaged,
        PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.PasswordRequired,
        PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.UnsupportedSecurity,
        _ => PdfOpenError.Unknown,
    };
}
