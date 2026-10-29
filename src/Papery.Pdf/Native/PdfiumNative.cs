using System.Runtime.InteropServices;

namespace Papery.Pdf.Native;

// Raw P/Invoke declarations for pdfium.dll.
//
// Signatures are copied from the C headers shipped in the bblanchon.PDFium.Win32
// package (build/native/include/pdfium/*.h). Two C types to watch on Windows:
//   - "unsigned long" is 32-bit, so it maps to C# uint (not ulong).
//   - FPDF_BOOL is a C int: 0 = false, non-zero = true.
//
// IMPORTANT: PDFium is not thread-safe. Never call these directly from app code;
// go through PdfiumThread so every call runs on the single PDFium thread.
// Only add functions here when they are actually used.
internal static unsafe partial class PdfiumNative
{
    private const string Dll = "pdfium";

    // ---- Library lifetime (fpdfview.h) ----

    [LibraryImport(Dll)]
    internal static partial void FPDF_InitLibrary();

    [LibraryImport(Dll)]
    internal static partial void FPDF_DestroyLibrary();

    [LibraryImport(Dll)]
    internal static partial uint FPDF_GetLastError();

    // ---- Documents (fpdfview.h) ----

    // FPDF_FILEACCESS: PDFium asks us for byte ranges instead of loading the whole file.
    // This is what lets a 300 MB PDF open without reading 300 MB from disk.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, uint, int> m_GetBlock;
        public nint m_Param;
    }

    [LibraryImport(Dll)]
    internal static partial nint FPDF_LoadCustomDocument(FPDF_FILEACCESS* fileAccess, byte* password);

    [LibraryImport(Dll)]
    internal static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Dll)]
    internal static partial int FPDF_GetPageCount(nint document);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    // Size of a page in points (1/72 inch), WITHOUT loading the page contents.
    [LibraryImport(Dll)]
    internal static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, FS_SIZEF* size);

    [LibraryImport(Dll)]
    internal static partial int FPDF_GetFileVersion(nint document, int* fileVersion);

    // -1 when the document is not encrypted.
    [LibraryImport(Dll)]
    internal static partial int FPDF_GetSecurityHandlerRevision(nint document);

    [LibraryImport(Dll)]
    internal static partial uint FPDF_GetDocPermissions(nint document);

    // false when PDFium had to rebuild a broken cross-reference table (i.e. the file was repaired).
    [LibraryImport(Dll)]
    internal static partial int FPDF_DocumentHasValidCrossReferenceTable(nint document);

    // ---- Pages and rendering (fpdfview.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Dll)]
    internal static partial void FPDF_ClosePage(nint page);

    internal const int FPDFBitmap_BGRA = 4;

    [LibraryImport(Dll)]
    internal static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Dll)]
    internal static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Dll)]
    internal static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Dll)]
    internal static partial void FPDFBitmap_Destroy(nint bitmap);

    // Render flags (fpdfview.h)
    internal const int FPDF_ANNOT = 0x01;
    internal const int FPDF_LCD_TEXT = 0x02;
    internal const int FPDF_GRAYSCALE = 0x08;
    internal const int FPDF_PRINTING = 0x800;

    [LibraryImport(Dll)]
    internal static partial void FPDF_RenderPageBitmap(
        nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---- Text (fpdf_text.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Dll)]
    internal static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_CountChars(nint textPage);

    // Writes UTF-16 into result; returns the number of chars written INCLUDING the terminator.
    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);

    // ---- Structure (fpdf_doc.h, fpdf_annot.h, fpdf_formfill.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetAnnotCount(nint page);

    // 0 = no form, 1 = AcroForm, 2 = XFA full, 3 = XFA foreground.
    [LibraryImport(Dll)]
    internal static partial int FPDF_GetFormType(nint document);

    [LibraryImport(Dll)]
    internal static partial uint FPDF_GetMetaText(nint document, byte* tag, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial uint FPDF_GetPageLabel(nint document, int pageIndex, void* buffer, uint bufferLength);

    // ---- Coordinates (fpdfview.h) ----

    // Converts a point in page space (points, origin bottom-left) to device pixels
    // for a page rendered at (startX, startY, sizeX, sizeY). Handles /Rotate and the crop box.
    [LibraryImport(Dll)]
    internal static partial int FPDF_PageToDevice(
        nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_MATRIX
    {
        public float A, B, C, D, E, F;
    }

    // The reverse of FPDF_PageToDevice: device pixels to page space.
    [LibraryImport(Dll)]
    internal static partial int FPDF_DeviceToPage(
        nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, double* pageX, double* pageY);

    // Index of the character at a point in page space, or -1 (fpdf_text.h).
    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTolerance, double yTolerance);

    // ---- Printing (fpdfview.h, Windows only) ----

    // Renders a page straight onto a GDI device context (a printer), as vectors where possible.
    [LibraryImport(Dll)]
    internal static partial int FPDF_RenderPage(
        nint hdc, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // ---- Search (fpdf_text.h) ----

    internal const uint FPDF_MATCHCASE = 0x1;
    internal const uint FPDF_MATCHWHOLEWORD = 0x2;

    [LibraryImport(Dll)]
    internal static partial nint FPDFText_FindStart(nint textPage, char* findWhat, uint flags, int startIndex);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_FindNext(nint handle);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetSchResultIndex(nint handle);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetSchCount(nint handle);

    [LibraryImport(Dll)]
    internal static partial void FPDFText_FindClose(nint handle);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);

    // ---- Plain-text web addresses found in page text (fpdf_text.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_LoadWebLinks(nint textPage);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_CountWebLinks(nint linkPage);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_GetURL(nint linkPage, int linkIndex, char* buffer, int bufferLength);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_CountRects(nint linkPage, int linkIndex);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_GetRect(nint linkPage, int linkIndex, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Dll)]
    internal static partial void FPDFLink_CloseWebLinks(nint linkPage);

    // ---- Link annotations, destinations and actions (fpdf_doc.h) ----

    internal const uint PDFACTION_GOTO = 1;
    internal const uint PDFACTION_REMOTEGOTO = 2;
    internal const uint PDFACTION_URI = 3;

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_Enumerate(nint page, int* startPos, nint* linkAnnot);

    [LibraryImport(Dll)]
    internal static partial int FPDFLink_GetAnnotRect(nint linkAnnot, FS_RECTF* rect);

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Dll)]
    internal static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Dll)]
    internal static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAction_GetURIPath(nint document, nint action, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    [LibraryImport(Dll)]
    internal static partial int FPDFDest_GetLocationInPage(
        nint dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(Dll)]
    internal static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufferLength);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Dll)]
    internal static partial nint FPDFBookmark_GetAction(nint bookmark);

    // ---- Page objects (fpdf_edit.h) ----

    internal const int FPDF_PAGEOBJ_IMAGE = 3;
    internal const int FPDF_PAGEOBJ_FORM = 5;

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_CountObjects(nint page);

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_GetObject(nint page, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_GetType(nint pageObject);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_GetMatrix(nint pageObject, FS_MATRIX* matrix);

    [LibraryImport(Dll)]
    internal static partial int FPDFFormObj_CountObjects(nint formObject);

    [LibraryImport(Dll)]
    internal static partial nint FPDFFormObj_GetObject(nint formObject, uint index);

    // ---- Error codes returned by FPDF_GetLastError ----
    internal const uint FPDF_ERR_SUCCESS = 0;
    internal const uint FPDF_ERR_UNKNOWN = 1;
    internal const uint FPDF_ERR_FILE = 2;
    internal const uint FPDF_ERR_FORMAT = 3;
    internal const uint FPDF_ERR_PASSWORD = 4;
    internal const uint FPDF_ERR_SECURITY = 5;
    internal const uint FPDF_ERR_PAGE = 6;
}
