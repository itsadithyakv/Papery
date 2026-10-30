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

    // ---- Page organising (fpdf_edit.h, fpdf_ppo.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDF_CreateNewDocument();

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_Delete(nint document, int pageIndex);

    // Moves the listed pages (no duplicates) so they appear in that order starting at destPageIndex.
    [LibraryImport(Dll)]
    internal static partial int FPDF_MovePages(nint document, int* pageIndices, uint length, int destPageIndex);

    // Rotation in quarter turns clockwise: 0..3.
    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetRotation(nint page, int rotate);

    // Deep-copies pages (and everything they use) from one document into another.
    [LibraryImport(Dll)]
    internal static partial int FPDF_ImportPagesByIndex(nint destDocument, nint sourceDocument, int* pageIndices, uint length, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDF_CopyViewerPreferences(nint destDocument, nint sourceDocument);

    // ---- Page boxes and transforms (fpdf_transformpage.h) ----
    // Boxes are in PDF user space: points, y pointing up, before the page's /Rotate.

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetMediaBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetBleedBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetTrimBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_SetArtBox(nint page, float left, float bottom, float right, float top);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetMediaBox(nint page, float* left, float* bottom, float* right, float* top);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_GetCropBox(nint page, float* left, float* bottom, float* right, float* top);

    // Wraps the page's content in "q <clip> <matrix> cm ... Q". The clip is in the new (transformed) space.
    [LibraryImport(Dll)]
    internal static partial int FPDFPage_TransFormWithClip(nint page, FS_MATRIX* matrix, FS_RECTF* clipRect);

    // A new document with N source pages drawn on each sheet (for printing; annotations are not carried over).
    [LibraryImport(Dll)]
    internal static partial nint FPDF_ImportNPagesToOne(nint sourceDocument, float outputWidth, float outputHeight, nuint pagesOnX, nuint pagesOnY);

    // ---- Annotations, only their rectangles (fpdf_annot.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Dll)]
    internal static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetRect(nint annotation, FS_RECTF* rect);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetRect(nint annotation, FS_RECTF* rect);

    // ---- Creating and changing annotations (fpdf_annot.h) ----

    internal const int FPDF_ANNOT_TEXT = 1;
    internal const int FPDF_ANNOT_LINK = 2;
    internal const int FPDF_ANNOT_FREETEXT = 3;
    internal const int FPDF_ANNOT_LINE = 4;
    internal const int FPDF_ANNOT_SQUARE = 5;
    internal const int FPDF_ANNOT_CIRCLE = 6;
    internal const int FPDF_ANNOT_POLYGON = 7;
    internal const int FPDF_ANNOT_POLYLINE = 8;
    internal const int FPDF_ANNOT_HIGHLIGHT = 9;
    internal const int FPDF_ANNOT_UNDERLINE = 10;
    internal const int FPDF_ANNOT_SQUIGGLY = 11;
    internal const int FPDF_ANNOT_STRIKEOUT = 12;
    internal const int FPDF_ANNOT_STAMP = 13;
    internal const int FPDF_ANNOT_INK = 15;
    internal const int FPDF_ANNOT_POPUP = 16;
    internal const int FPDF_ANNOT_WIDGET = 20;

    internal const int FPDF_ANNOT_FLAG_HIDDEN = 1 << 1;
    internal const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    internal const int FPDFANNOT_COLORTYPE_Color = 0;
    internal const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_POINTF
    {
        public float X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_QUADPOINTSF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    [LibraryImport(Dll)]
    internal static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_RemoveAnnot(nint page, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetSubtype(nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetColor(nint annotation, int type, uint r, uint g, uint b, uint a);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetColor(nint annotation, int type, uint* r, uint* g, uint* b, uint* a);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_AppendAttachmentPoints(nint annotation, FS_QUADPOINTSF* points);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_AddInkStroke(nint annotation, FS_POINTF* points, nuint count);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetBorder(nint annotation, float horizontalRadius, float verticalRadius, float width);

    // Strings are UTF-16LE, null-terminated; keys are ASCII ("Contents", "T", "Subj", "NM", "M").
    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetStringValue(nint annotation, byte* key, char* value);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAnnot_GetStringValue(nint annotation, byte* key, char* buffer, uint bufferBytes);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetAP(nint annotation, int mode, char* value);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFlags(nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_SetFlags(nint annotation, int flags);

    // Only for stamp and ink annotations. The annotation takes ownership of the object.
    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_AppendObject(nint annotation, nint pageObject);

    // ---- Page objects, for stamps and text boxes (fpdf_edit.h) ----

    internal const int FPDF_FILLMODE_NONE = 0;
    internal const int FPDF_FILLMODE_WINDING = 2;
    internal const int FPDF_FONT_TRUETYPE = 2;

    [LibraryImport(Dll)]
    internal static partial nint FPDFPageObj_CreateNewPath(float x, float y);

    [LibraryImport(Dll)]
    internal static partial int FPDFPath_MoveTo(nint path, float x, float y);

    [LibraryImport(Dll)]
    internal static partial int FPDFPath_LineTo(nint path, float x, float y);

    [LibraryImport(Dll)]
    internal static partial int FPDFPath_Close(nint path);

    [LibraryImport(Dll)]
    internal static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_SetStrokeColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Dll)]
    internal static partial int FPDFPageObj_SetStrokeWidth(nint pageObject, float width);

    [LibraryImport(Dll)]
    internal static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport(Dll)]
    internal static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Dll)]
    internal static partial nint FPDFText_LoadStandardFont(nint document, byte* font);

    [LibraryImport(Dll)]
    internal static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);

    [LibraryImport(Dll)]
    internal static partial void FPDFFont_Close(nint font);

    [LibraryImport(Dll)]
    internal static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);

    [LibraryImport(Dll)]
    internal static partial int FPDFText_SetText(nint textObject, char* text);

        // ---- Form filling (fpdf_formfill.h) ----

    internal const int FPDF_FORMFIELD_PUSHBUTTON = 1;
    internal const int FPDF_FORMFIELD_CHECKBOX = 2;
    internal const int FPDF_FORMFIELD_RADIOBUTTON = 3;
    internal const int FPDF_FORMFIELD_COMBOBOX = 4;
    internal const int FPDF_FORMFIELD_LISTBOX = 5;
    internal const int FPDF_FORMFIELD_TEXTFIELD = 6;
    internal const int FPDF_FORMFIELD_SIGNATURE = 7;
    internal const int FPDF_FORMFLAG_READONLY = 1 << 0;
    internal const int FPDF_FORMFLAG_TEXT_MULTILINE = 1 << 12;
    internal const int FPDF_FORMFLAG_TEXT_PASSWORD = 1 << 13;
    internal const int FLAT_NORMALDISPLAY = 0;

    [LibraryImport(Dll)]
    internal static partial nint FPDFDOC_InitFormFillEnvironment(nint document, void* formInfo);

    [LibraryImport(Dll)]
    internal static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    [LibraryImport(Dll)]
    internal static partial void FORM_OnAfterLoadPage(nint page, nint form);

    [LibraryImport(Dll)]
    internal static partial void FORM_OnBeforeClosePage(nint page, nint form);

    [LibraryImport(Dll)]
    internal static partial int FORM_OnLButtonDown(nint form, nint page, int modifier, double x, double y);

    [LibraryImport(Dll)]
    internal static partial int FORM_OnLButtonUp(nint form, nint page, int modifier, double x, double y);

    [LibraryImport(Dll)]
    internal static partial int FORM_SetFocusedAnnot(nint form, nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FORM_SelectAllText(nint form, nint page);

    [LibraryImport(Dll)]
    internal static partial void FORM_ReplaceSelection(nint form, nint page, char* text);

    [LibraryImport(Dll)]
    internal static partial int FORM_ForceToKillFocus(nint form);

    [LibraryImport(Dll)]
    internal static partial int FORM_SetIndexSelected(nint form, nint page, int index, int selected);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFormFieldType(nint form, nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetFormFieldFlags(nint form, nint annotation);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAnnot_GetFormFieldName(nint form, nint annotation, char* buffer, uint bufferBytes);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAnnot_GetFormFieldValue(nint form, nint annotation, char* buffer, uint bufferBytes);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_GetOptionCount(nint form, nint annotation);

    [LibraryImport(Dll)]
    internal static partial uint FPDFAnnot_GetOptionLabel(nint form, nint annotation, int index, char* buffer, uint bufferBytes);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_IsOptionSelected(nint form, nint annotation, int index);

    [LibraryImport(Dll)]
    internal static partial int FPDFAnnot_IsChecked(nint form, nint annotation);

    [LibraryImport(Dll)]
    internal static partial int FPDFPage_Flatten(nint page, int flag);

    // ---- Images in page objects (fpdf_edit.h) ----

    [LibraryImport(Dll)]
    internal static partial nint FPDFPageObj_NewImageObj(nint document);

    [LibraryImport(Dll)]
    internal static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);

    [LibraryImport(Dll)]
    internal static partial int FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);

    // ---- Saving (fpdf_save.h) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEWRITE
    {
        public int version; // must be 1
        public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, byte*, uint, int> WriteBlock;
    }

    internal const uint FPDF_INCREMENTAL = 1 << 0;
    internal const uint FPDF_NO_INCREMENTAL = 1 << 1;
    internal const uint FPDF_REMOVE_SECURITY = 1 << 2;

    [LibraryImport(Dll)]
    internal static partial int FPDF_SaveAsCopy(nint document, FPDF_FILEWRITE* fileWrite, uint flags);

    // ---- Error codes returned by FPDF_GetLastError ----
    internal const uint FPDF_ERR_SUCCESS = 0;
    internal const uint FPDF_ERR_UNKNOWN = 1;
    internal const uint FPDF_ERR_FILE = 2;
    internal const uint FPDF_ERR_FORMAT = 3;
    internal const uint FPDF_ERR_PASSWORD = 4;
    internal const uint FPDF_ERR_SECURITY = 5;
    internal const uint FPDF_ERR_PAGE = 6;
}
