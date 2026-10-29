using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// An open PDF file. All methods are safe to call from any thread: the work is
/// forwarded to the PDFium thread and the result comes back as a Task.
/// </summary>
/// <remarks>
/// This class is split into partial files by topic:
/// PdfDocument.Rendering.cs, PdfDocument.Text.cs, PdfDocument.Navigation.cs, PdfDocument.Printing.cs.
/// </remarks>
public sealed partial class PdfDocument : IAsyncDisposable
{
    // Loaded page handles kept open so rendering several tiles of a page doesn't re-parse it.
    private const int PageCacheSize = 6;

    private readonly PdfiumThread _thread;
    private readonly PdfFileSource _source;
    private readonly LinkedList<(int Index, nint Handle)> _pageCache = new(); // most recent first; PDFium thread only
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
            sizes[i] = PdfiumNative.FPDF_GetPageSizeByIndexF(handle, i, &size) != 0 && size.Width > 0 && size.Height > 0
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

    /// <summary>Bit 3 of the permissions: printing allowed.</summary>
    public bool CanPrint => (Permissions & (1 << 2)) != 0;

    /// <summary>Bit 5 of the permissions: copying text allowed.</summary>
    public bool CanCopy => (Permissions & (1 << 4)) != 0;

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
        }, PdfWorkPriority.Visible, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle == 0)
        {
            return;
        }

        await _thread.InvokeAsync(() =>
        {
            CloseTextPage();
            foreach (var (_, page) in _pageCache)
            {
                PdfiumNative.FPDF_ClosePage(page);
            }

            _pageCache.Clear();
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = 0;
        }).ConfigureAwait(false);

        // Only free the file access struct after PDFium has let go of it.
        _source.Dispose();
    }

    /// <summary>Runs work on the PDFium thread with a loaded page. Must only be called from inside the thread.</summary>
    private T WithPage<T>(int pageIndex, Func<nint, T> work) => work(GetPage(pageIndex));

    private nint GetPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        for (var node = _pageCache.First; node != null; node = node.Next)
        {
            if (node.Value.Index == pageIndex)
            {
                _pageCache.Remove(node);
                _pageCache.AddFirst(node);
                return node.Value.Handle;
            }
        }

        nint page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
        if (page == 0)
        {
            throw new InvalidDataException($"Page {pageIndex + 1} of '{FilePath}' could not be loaded.");
        }

        _pageCache.AddFirst((pageIndex, page));
        if (_pageCache.Count > PageCacheSize)
        {
            var (evictedIndex, evictedHandle) = _pageCache.Last!.Value;
            if (evictedIndex == _textPageIndex)
            {
                CloseTextPage(); // a text page must be closed before its page
            }

            PdfiumNative.FPDF_ClosePage(evictedHandle);
            _pageCache.RemoveLast();
        }

        return page;
    }

    private Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken, PdfWorkPriority priority = PdfWorkPriority.Visible) =>
        _thread.InvokeAsync(work, priority, cancellationToken);

    private void CheckPageIndex(int pageIndex) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)pageIndex, (uint)PageCount, nameof(pageIndex));

    /// <summary>Converts a rectangle in page space (points, y up) to fractions of the displayed page.</summary>
    private static unsafe NormalizedRect Normalize(nint page, double left, double top, double right, double bottom)
    {
        const int Scale = 100_000;
        int x1, y1, x2, y2;
        _ = PdfiumNative.FPDF_PageToDevice(page, 0, 0, Scale, Scale, 0, left, top, &x1, &y1);
        _ = PdfiumNative.FPDF_PageToDevice(page, 0, 0, Scale, Scale, 0, right, bottom, &x2, &y2);
        double x = Math.Min(x1, x2) / (double)Scale, y = Math.Min(y1, y2) / (double)Scale;
        return new NormalizedRect(x, y, Math.Abs(x2 - x1) / (double)Scale, Math.Abs(y2 - y1) / (double)Scale);
    }

    private static PdfOpenError MapError(uint error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => PdfOpenError.FileUnreadable,
        PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.Damaged,
        PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.PasswordRequired,
        PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.UnsupportedSecurity,
        _ => PdfOpenError.Unknown,
    };
}
