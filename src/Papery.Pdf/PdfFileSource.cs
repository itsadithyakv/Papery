using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// Feeds a file to PDFium on demand, one byte range at a time.
/// </summary>
/// <remarks>
/// PDFium calls <see cref="GetBlock"/> whenever it needs bytes. We answer with a
/// positional read (no shared seek pointer), so nothing is loaded up front and a
/// 300 MB file costs only what is actually displayed.
///
/// The file is opened with FileShare.ReadWrite | FileShare.Delete so other apps
/// (and OneDrive sync) are not blocked while Papery has the document open.
/// </remarks>
internal sealed unsafe class PdfFileSource : IDisposable
{
    private readonly SafeFileHandle _file;
    private GCHandle _self;
    private PdfiumNative.FPDF_FILEACCESS* _access;

    public PdfFileSource(string path)
    {
        _file = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);

        Length = RandomAccess.GetLength(_file);
        if (Length > uint.MaxValue)
        {
            _file.Dispose();
            throw new PdfOpenException(PdfOpenError.TooLarge, path);
        }

        // PDFium keeps this pointer for the lifetime of the document, so the struct
        // lives in native memory and "this" is pinned via a GCHandle passed as m_Param.
        _self = GCHandle.Alloc(this);
        _access = (PdfiumNative.FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(PdfiumNative.FPDF_FILEACCESS));
        _access->m_FileLen = (uint)Length;
        _access->m_GetBlock = &GetBlock;
        _access->m_Param = GCHandle.ToIntPtr(_self);
    }

    public long Length { get; }

    internal PdfiumNative.FPDF_FILEACCESS* Access => _access;

    public void Dispose()
    {
        if (_access != null)
        {
            NativeMemory.Free(_access);
            _access = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _file.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        // Exceptions must never cross back into native code: report failure as 0 instead.
        try
        {
            var source = (PdfFileSource)GCHandle.FromIntPtr(param).Target!;
            var span = new Span<byte>(buffer, checked((int)size));
            long offset = position;
            while (!span.IsEmpty)
            {
                int read = RandomAccess.Read(source._file, span, offset);
                if (read <= 0)
                {
                    return 0;
                }

                span = span[read..];
                offset += read;
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }
}
