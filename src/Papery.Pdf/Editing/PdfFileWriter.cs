using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// Receives the bytes PDFium produces when saving and writes them to a stream.
/// </summary>
/// <remarks>
/// PDFium calls WriteBlock with a pointer to the FPDF_FILEWRITE it was given. We allocate
/// that struct in native memory with one extra field after it (a GCHandle to this object),
/// so the callback can find its way back without any global state.
/// </remarks>
internal sealed unsafe class PdfFileWriter : IDisposable
{
    private readonly Stream _stream;
    private GCHandle _self;
    private Block* _block;

    public PdfFileWriter(Stream stream)
    {
        _stream = stream;
        _self = GCHandle.Alloc(this);
        _block = (Block*)NativeMemory.AllocZeroed((nuint)sizeof(Block));
        _block->Write.version = 1;
        _block->Write.WriteBlock = &WriteBlock;
        _block->Self = GCHandle.ToIntPtr(_self);
    }

    /// <summary>The exception the stream threw, if writing failed (e.g. disk full).</summary>
    public Exception? Error { get; private set; }

    internal PdfiumNative.FPDF_FILEWRITE* Native => &_block->Write;

    public void Dispose()
    {
        if (_block != null)
        {
            NativeMemory.Free(_block);
            _block = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(PdfiumNative.FPDF_FILEWRITE* write, byte* data, uint size)
    {
        // Exceptions must never cross back into native code: remember them and report failure.
        PdfFileWriter? writer = null;
        try
        {
            writer = (PdfFileWriter)GCHandle.FromIntPtr(((Block*)write)->Self).Target!;
            if (writer.Error != null)
            {
                return 0;
            }

            writer._stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception ex)
        {
            if (writer != null)
            {
                writer.Error = ex;
            }

            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public PdfiumNative.FPDF_FILEWRITE Write; // must stay first: PDFium hands this address back
        public nint Self;
    }
}
