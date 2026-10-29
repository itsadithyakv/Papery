namespace Papery.Pdf;

/// <summary>Page size in PDF points (1 pt = 1/72 inch), already accounting for page rotation.</summary>
public readonly record struct PdfPageSize(float Width, float Height);

/// <summary>BGRA pixels, 4 bytes per pixel, rows <see cref="Stride"/> bytes apart.</summary>
public sealed record PdfBitmap(int Width, int Height, int Stride, byte[] Pixels);

/// <summary>
/// A rectangle on a page as fractions of the page as displayed: (0,0) is the top-left
/// corner, (1,1) the bottom-right, after rotation. The UI multiplies by the on-screen
/// page size, so these never need converting again when zooming.
/// </summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>A rectangle of pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

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
    Annotations = Native.PdfiumNative.FPDF_ANNOT,

    /// <summary>Render as greyscale.</summary>
    Grayscale = Native.PdfiumNative.FPDF_GRAYSCALE,

    /// <summary>Use print appearance (hides "screen only" annotations).</summary>
    Printing = Native.PdfiumNative.FPDF_PRINTING,
}

/// <summary>A clickable area on a page: either a jump inside the document or a web address.</summary>
public sealed record PdfLink(NormalizedRect Area, int? TargetPage, double? TargetY, string? Uri);

/// <summary>One bookmark in the document outline.</summary>
public sealed record PdfOutlineItem(string Title, int? PageIndex, IReadOnlyList<PdfOutlineItem> Children);

[Flags]
public enum PdfSearchOptions
{
    None = 0,
    MatchCase = 1,
    WholeWord = 2,
}

/// <summary>One search match, with the rectangles to highlight and a bit of surrounding text.</summary>
public sealed record PdfSearchHit(int PageIndex, int CharIndex, int Length, IReadOnlyList<NormalizedRect> Areas, string Context);

/// <summary>Document information shown in Properties.</summary>
public sealed record PdfMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);
