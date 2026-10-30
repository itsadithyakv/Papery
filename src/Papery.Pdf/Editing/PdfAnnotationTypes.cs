namespace Papery.Pdf.Editing;

/// <summary>What an annotation is, as far as the user is concerned.</summary>
public enum PdfAnnotationKind
{
    Highlight,
    Underline,
    Squiggly,
    StrikeOut,
    Note,
    Pen,
    Marker,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Polygon,
    Cloud,
    TextBox,
    Callout,
    Stamp,
    Measure,

    /// <summary>Something another app made that Papery doesn't have a tool for.</summary>
    Other,
}

public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255);

/// <summary>A point on a page as fractions of the page as displayed, with pen pressure (0-1; 1 when there's none).</summary>
public readonly record struct PagePoint(double X, double Y, float Pressure = 1);

/// <summary>
/// A new annotation, described the way the user sees the page. Which fields matter depends on the kind:
/// text marks use <see cref="Areas"/> (one per line of text); shapes, notes, stamps and text boxes use the
/// first area; pens, lines, arrows, polygons and measures use <see cref="Strokes"/>; a callout uses both
/// (its box, then the line to what it points at).
/// </summary>
public sealed record PdfAnnotationDraft(
    PdfAnnotationKind Kind,
    PdfColor Color,
    IReadOnlyList<NormalizedRect>? Areas = null,
    IReadOnlyList<IReadOnlyList<PagePoint>>? Strokes = null,
    float Width = 1.5f,
    string? Text = null,
    float FontSize = 12,
    string? Author = null);

/// <summary>An annotation on a page, as read back from the document.</summary>
/// <param name="Name">Its unique name in the file (the PDF /NM entry); how Papery refers to it.</param>
/// <param name="Bounds">Its rectangle as fractions of the page as displayed.</param>
public sealed record PdfAnnotationInfo(
    string Name,
    int PageIndex,
    PdfAnnotationKind Kind,
    PdfColor Color,
    NormalizedRect Bounds,
    string? Text,
    string? Author);
