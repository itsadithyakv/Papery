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

    /// <summary>A picture placed on the page (a signature or initials). Its first area is where.</summary>
    Signature,

    /// <summary>Something another app made that Papery doesn't have a tool for.</summary>
    Other,

    /// <summary>A box of solid colour over part of the page. It covers; what's underneath is still in the file.</summary>
    Whiteout,

    /// <summary>A picture added to the page. Its first area is where.</summary>
    Image,
}

/// <summary>The three families every PDF viewer has, so added text looks the same everywhere.</summary>
public enum PdfTextFont
{
    Sans,
    Serif,
    Mono,
}

public enum PdfTextAlign
{
    Left,
    Centre,
    Right,
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
    string? Author = null,
    PdfBitmap? Picture = null,
    float RotationDegrees = 0,
    PdfTextFont Font = PdfTextFont.Sans,
    bool Bold = false,
    bool Italic = false,
    PdfTextAlign Align = PdfTextAlign.Left);

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

public enum PdfFormFieldKind
{
    Text,
    CheckBox,
    RadioButton,
    ComboBox,
    ListBox,
    Signature,
    Button,
}

/// <summary>One fillable widget on a page, as the user sees it.</summary>
/// <param name="Index">Its position among the page's annotations; how Papery finds it again.</param>
public sealed record PdfFormField(
    int PageIndex,
    int Index,
    string Name,
    PdfFormFieldKind Kind,
    NormalizedRect Bounds,
    string Value,
    bool IsChecked,
    bool IsReadOnly,
    bool IsMultiline,
    bool IsPassword,
    IReadOnlyList<string> Options,
    int SelectedOption);
