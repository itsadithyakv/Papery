using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// Applies a <see cref="PageGeometry"/> to a loaded PDFium page, in place. PDFium thread only.
/// </summary>
/// <remarks>
/// Everything happens in PDF user space (points, y up, before /Rotate). Display-space requests
/// are converted with PDFium's own device mapping, so rotated pages and odd crop-box origins are
/// handled exactly as the viewer shows them.
///
/// Cropping sets the CropBox: like every PDF editor, it hides the rest of the page rather than
/// deleting it (Redact is the tool that really removes content). A new paper size or margins move
/// and scale the content with one matrix and set every page box to the new paper, and the same
/// matrix moves the annotations (links, comments, form fields), so they stay where they belong.
/// </remarks>
internal static unsafe class PageGeometryApplier
{
    private const int DeviceScale = 1_000_000;

    public static void Apply(nint page, PageGeometry geometry)
    {
        if (geometry.StraightenDegrees != 0)
        {
            Straighten(page, geometry.StraightenDegrees);
        }

        if (geometry.Crop is { } crop)
        {
            var (left, bottom, right, top) = ToUserSpace(page, PageGeometry.Clamp(crop));
            var visible = VisibleBox(page);
            // Never grow past what was visible before.
            PdfiumNative.FPDFPage_SetCropBox(
                page,
                Math.Max(left, visible.Left),
                Math.Max(bottom, visible.Bottom),
                Math.Min(right, visible.Right),
                Math.Min(top, visible.Top));
        }

        if (geometry.ChangesPaper)
        {
            PutOnPaper(page, geometry);
        }
    }

    /// <summary>The CropBox if the page has one (it's what viewers show), otherwise the MediaBox.</summary>
    internal static (float Left, float Bottom, float Right, float Top) VisibleBox(nint page)
    {
        float l, b, r, t;
        if (PdfiumNative.FPDFPage_GetCropBox(page, &l, &b, &r, &t) != 0 || PdfiumNative.FPDFPage_GetMediaBox(page, &l, &b, &r, &t) != 0)
        {
            return (Math.Min(l, r), Math.Min(b, t), Math.Max(l, r), Math.Max(b, t));
        }

        return (0, 0, 612, 792);
    }

    /// <summary>A rectangle in display fractions to user space (handles /Rotate and crop-box offsets).</summary>
    private static (float Left, float Bottom, float Right, float Top) ToUserSpace(nint page, NormalizedRect rect)
    {
        double x1, y1, x2, y2;
        _ = PdfiumNative.FPDF_DeviceToPage(page, 0, 0, DeviceScale, DeviceScale, 0, (int)(rect.X * DeviceScale), (int)(rect.Y * DeviceScale), &x1, &y1);
        _ = PdfiumNative.FPDF_DeviceToPage(page, 0, 0, DeviceScale, DeviceScale, 0, (int)(rect.Right * DeviceScale), (int)(rect.Bottom * DeviceScale), &x2, &y2);
        return ((float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Max(x1, x2), (float)Math.Max(y1, y2));
    }

    /// <summary>Turns the content clockwise (as seen on screen) about the middle of the visible page.</summary>
    private static void Straighten(nint page, float degrees)
    {
        var box = VisibleBox(page);
        double cx = (box.Left + box.Right) / 2, cy = (box.Bottom + box.Top) / 2;

        // Clockwise on screen is a negative angle in PDF space (y points up there); /Rotate doesn't change that.
        double angle = -degrees * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var matrix = new PdfiumNative.FS_MATRIX
        {
            A = (float)cos,
            B = (float)sin,
            C = (float)-sin,
            D = (float)cos,
            E = (float)(cx - (cos * cx) + (sin * cy)),
            F = (float)(cy - (sin * cx) - (cos * cy)),
        };
        var clip = new PdfiumNative.FS_RECTF { Left = box.Left, Bottom = box.Bottom, Right = box.Right, Top = box.Top };
        _ = PdfiumNative.FPDFPage_TransFormWithClip(page, &matrix, &clip);
    }

    /// <summary>Moves and scales the visible part of the page onto new paper, centred inside the margins.</summary>
    private static void PutOnPaper(nint page, PageGeometry geometry)
    {
        var box = VisibleBox(page);
        double contentW = box.Right - box.Left, contentH = box.Top - box.Bottom;
        if (contentW <= 0 || contentH <= 0)
        {
            return;
        }

        // Paper is given as displayed; on a page turned a quarter, its width runs along user-space y.
        bool sideways = (PdfiumNative.FPDFPage_GetRotation(page) & 1) == 1;
        double margin = Math.Max(0, geometry.Margin);
        double paperW, paperH;
        if (geometry.Paper is { } paper)
        {
            (paperW, paperH) = sideways ? (paper.Height, paper.Width) : (paper.Width, paper.Height);
        }
        else
        {
            (paperW, paperH) = (contentW + (2 * margin), contentH + (2 * margin));
        }

        double scale = PageGeometry.Scale(contentW, contentH, paperW, paperH, margin, geometry.FitContent);
        double cx = (box.Left + box.Right) / 2, cy = (box.Bottom + box.Top) / 2;
        var matrix = new PdfiumNative.FS_MATRIX
        {
            A = (float)scale,
            D = (float)scale,
            E = (float)((paperW / 2) - (scale * cx)),
            F = (float)((paperH / 2) - (scale * cy)),
        };

        // Clip to where the old visible area lands, so nothing that was cropped away reappears.
        var clip = Transform(matrix, box.Left, box.Bottom, box.Right, box.Top);
        _ = PdfiumNative.FPDFPage_TransFormWithClip(page, &matrix, &clip);

        float w = (float)paperW, h = (float)paperH;
        PdfiumNative.FPDFPage_SetMediaBox(page, 0, 0, w, h);
        PdfiumNative.FPDFPage_SetCropBox(page, 0, 0, w, h);
        PdfiumNative.FPDFPage_SetBleedBox(page, 0, 0, w, h);
        PdfiumNative.FPDFPage_SetTrimBox(page, 0, 0, w, h);
        PdfiumNative.FPDFPage_SetArtBox(page, 0, 0, w, h);
        MoveAnnotations(page, matrix);
    }

    private static void MoveAnnotations(nint page, PdfiumNative.FS_MATRIX matrix)
    {
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annotation == 0)
            {
                continue;
            }

            PdfiumNative.FS_RECTF rect;
            if (PdfiumNative.FPDFAnnot_GetRect(annotation, &rect) != 0)
            {
                var moved = Transform(matrix, rect.Left, Math.Min(rect.Bottom, rect.Top), rect.Right, Math.Max(rect.Bottom, rect.Top));
                _ = PdfiumNative.FPDFAnnot_SetRect(annotation, &moved);
            }

            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>A scale-and-move matrix applied to a rectangle.</summary>
    private static PdfiumNative.FS_RECTF Transform(PdfiumNative.FS_MATRIX m, float left, float bottom, float right, float top) => new()
    {
        Left = (m.A * left) + m.E,
        Right = (m.A * right) + m.E,
        Bottom = (m.D * bottom) + m.F,
        Top = (m.D * top) + m.F,
    };
}
