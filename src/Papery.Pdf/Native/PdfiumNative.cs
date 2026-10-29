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

    // ---- Error codes returned by FPDF_GetLastError ----
    internal const uint FPDF_ERR_SUCCESS = 0;
    internal const uint FPDF_ERR_UNKNOWN = 1;
    internal const uint FPDF_ERR_FILE = 2;
    internal const uint FPDF_ERR_FORMAT = 3;
    internal const uint FPDF_ERR_PASSWORD = 4;
    internal const uint FPDF_ERR_SECURITY = 5;
    internal const uint FPDF_ERR_PAGE = 6;
}
