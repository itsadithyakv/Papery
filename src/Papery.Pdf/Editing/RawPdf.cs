using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// A PDFium document handle plus the file it reads from. Only used on the PDFium thread.
/// </summary>
internal sealed unsafe class RawPdf : IDisposable
{
    private readonly PdfFileSource? _source;

    private RawPdf(PdfFileSource? source, nint handle, string path)
    {
        _source = source;
        Handle = handle;
        FilePath = path;
    }

    public nint Handle { get; private set; }

    public string FilePath { get; }

    public int PageCount => PdfiumNative.FPDF_GetPageCount(Handle);

    public long FileLength => _source?.Length ?? 0;

    /// <summary>True when the owner allows assembling (insert, rotate, delete pages); PDF spec table 22, bits 4 and 11.</summary>
    public bool CanAssemble
    {
        get
        {
            uint p = PdfiumNative.FPDF_GetDocPermissions(Handle);
            return (p & (1 << 10)) != 0 || (p & (1 << 3)) != 0;
        }
    }

    /// <summary>Opens a file. Throws <see cref="PdfOpenException"/> with a clear reason on failure.</summary>
    public static RawPdf Load(string path, string? password)
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

        return new RawPdf(source, handle, path);
    }

    public static RawPdf CreateNew() => new(null, PdfiumNative.FPDF_CreateNewDocument(), string.Empty);

    /// <summary>Takes ownership of a document PDFium created in memory (e.g. by FPDF_ImportNPagesToOne).</summary>
    public static RawPdf Wrap(nint handle) => new(null, handle, string.Empty);

    /// <summary>
    /// Releases PDFium's handle and the file. The PdfFileSource must outlive the handle,
    /// because PDFium may read from it until FPDF_CloseDocument returns.
    /// </summary>
    public void Dispose()
    {
        if (Handle != 0)
        {
            PdfiumNative.FPDF_CloseDocument(Handle);
            Handle = 0;
        }

        _source?.Dispose();
    }

    internal static PdfOpenError MapError(uint error) => error switch
    {
        PdfiumNative.FPDF_ERR_FILE => PdfOpenError.FileUnreadable,
        PdfiumNative.FPDF_ERR_FORMAT => PdfOpenError.Damaged,
        PdfiumNative.FPDF_ERR_PASSWORD => PdfOpenError.PasswordRequired,
        PdfiumNative.FPDF_ERR_SECURITY => PdfOpenError.UnsupportedSecurity,
        _ => PdfOpenError.Unknown,
    };
}
