using System.Runtime.InteropServices;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// PDFium's form engine for one document. PDFium thread only. Pages must be attached after loading
/// and detached before closing; it draws form fields (normal rendering leaves them out) and fills them.
/// </summary>
/// <remarks>
/// The callback table is empty on purpose: Papery draws its own controls, and PDFium checks each
/// callback before using it. It must stay allocated for as long as the environment lives.
/// </remarks>
internal sealed unsafe class FormEnvironment : IDisposable
{
    private const int TableBytes = 1024; // bigger than FPDF_FORMFILLINFO in any PDFium version

    private void* _info;

    public FormEnvironment(nint document)
    {
        _info = NativeMemory.AllocZeroed(TableBytes);
        *(int*)_info = 1; // the interface version
        Handle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document, _info);
        if (Handle == 0)
        {
            NativeMemory.Free(_info);
            _info = null;
            throw new InvalidOperationException("The form engine could not start.");
        }
    }

    public nint Handle { get; private set; }

    public void Attach(nint page) => PdfiumNative.FORM_OnAfterLoadPage(page, Handle);

    public void Detach(nint page) => PdfiumNative.FORM_OnBeforeClosePage(page, Handle);

    public void Dispose()
    {
        if (Handle != 0)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(Handle);
            Handle = 0;
        }

        if (_info != null)
        {
            NativeMemory.Free(_info);
            _info = null;
        }
    }
}
