namespace Papery.Pdf.Editing;

/// <summary>A PDF used as input, with the password it was opened with (if any).</summary>
public sealed record PdfInputFile(string Path, string? Password = null);

/// <summary>
/// One page of a result: page <see cref="Page"/> of input <see cref="Source"/>, turned by
/// <see cref="QuarterTurns"/> extra quarter turns clockwise, then reshaped by <see cref="Geometry"/>
/// (crop, paper size, margins, straighten); or a blank page.
/// </summary>
public readonly record struct PageRef(int Source, int Page, int QuarterTurns = 0, PdfPageSize BlankSize = default, PageGeometry? Geometry = null)
{
    public bool IsBlank => Source < 0;

    public static PageRef Blank(PdfPageSize size) => new(-1, -1, 0, size);

    public PageRef Rotated(int quarterTurns) => this with { QuarterTurns = (((QuarterTurns + quarterTurns) % 4) + 4) % 4 };
}

/// <summary>
/// Describes a new PDF built from pages of other PDFs. Organise, merge, split and extract
/// are all expressed as one of these.
/// </summary>
/// <param name="Sources">The input files <see cref="PageRef.Source"/> refers to.</param>
/// <param name="Pages">The pages of the result, in order. Must not be empty.</param>
/// <param name="EditInPlaceOf">
/// When set, the result is that source with its pages changed, so everything else in it
/// (bookmarks, form fields, metadata, page labels, accessibility tags, password) is kept.
/// When null, the result is a brand-new document holding copies of the pages.
/// </param>
/// <param name="Outline">
/// Bookmarks to give a new document (page numbers refer to the result). Ignored when editing in place.
/// </param>
/// <param name="Title">Document title to give a new document, or null.</param>
public sealed record PdfComposeRequest(
    IReadOnlyList<PdfInputFile> Sources,
    IReadOnlyList<PageRef> Pages,
    int? EditInPlaceOf = null,
    IReadOnlyList<PdfOutlineItem>? Outline = null,
    string? Title = null);
