using Papery.Pdf.Editing;

namespace Papery.Pdf;

/// <summary>
/// Entry point to the PDF engine. There is exactly one per process, because
/// PDFium itself is a process-wide library with a single worker thread.
/// </summary>
public sealed class PdfEngine
{
    private PdfEngine()
    {
    }

    public static PdfEngine Shared { get; } = new();

    internal PdfiumThread Thread { get; } = new();

    /// <summary>Opens a PDF. Throws <see cref="PdfOpenException"/> with a clear reason on failure.</summary>
    public Task<PdfDocument> OpenAsync(string path, string? password = null, CancellationToken cancellationToken = default) =>
        PdfDocument.OpenAsync(this, path, password, cancellationToken);

    /// <summary>
    /// Builds a PDF from pages of other PDFs and saves it to <paramref name="outputPath"/> without
    /// ever leaving a half-written file. Throws <see cref="PdfOpenException"/> for unreadable inputs
    /// and <see cref="PdfEditException"/> for everything else.
    /// </summary>
    public Task<PdfComposeResult> ComposeAsync(
        PdfComposeRequest request,
        string outputPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        PdfComposer.ComposeAsync(Thread, request, outputPath, progress, cancellationToken);

    /// <summary>Adds new form fields to a saved PDF.</summary>
    public Task AddFormFieldsAsync(PdfInputFile input, IReadOnlyList<NewFormField> fields, string outputPath, CancellationToken cancellationToken = default) =>
        PdfFieldWriter.AddAsync(Thread, input, fields, outputPath, cancellationToken);

    /// <summary>Makes form answers and annotations a permanent part of the pages.</summary>
    public Task FlattenAsync(PdfInputFile input, string outputPath, CancellationToken cancellationToken = default) =>
        PdfComposer.FlattenAsync(Thread, input, outputPath, Native.PdfiumNative.FLAT_NORMALDISPLAY, cancellationToken);

    /// <summary>Writes numbers, a header or footer, a watermark or Bates numbers onto pages.</summary>
    public Task StampPagesAsync(PdfInputFile input, PdfPageStamp stamp, string outputPath, CancellationToken cancellationToken = default) =>
        PageStamper.ApplyAsync(Thread, input, stamp, outputPath, cancellationToken);

    /// <summary>Removes and adds link annotations on a saved PDF.</summary>
    public Task EditLinksAsync(PdfInputFile input, IReadOnlyList<PdfLinkRemoval> remove, IReadOnlyList<NewPdfLink> add, string outputPath, CancellationToken cancellationToken = default) =>
        PdfLinkWriter.ApplyAsync(Thread, input, remove, add, outputPath, cancellationToken);

    /// <summary>Replaces a saved PDF's bookmarks. An empty list removes them all.</summary>
    public Task SetOutlineAsync(PdfInputFile input, IReadOnlyList<PdfOutlineItem> outline, string outputPath, CancellationToken cancellationToken = default) =>
        PdfLinkWriter.SetOutlineAsync(Thread, input, outline, outputPath, cancellationToken);

    /// <summary>Puts a copy of an earlier version back in place, through the checked save (undo for whole-file changes).</summary>
    public Task RestoreCopyAsync(string copyPath, string? password, string outputPath, CancellationToken cancellationToken = default) =>
        PdfLinkWriter.RestoreAsync(Thread, copyPath, password, outputPath, cancellationToken);

    /// <summary>Puts <paramref name="across"/> × <paramref name="down"/> pages on each sheet of a new PDF (for printing).</summary>
    public Task<PdfComposeResult> NUpAsync(
        PdfInputFile input,
        string outputPath,
        PdfPageSize sheet,
        int across,
        int down,
        CancellationToken cancellationToken = default) =>
        PdfComposer.NUpAsync(Thread, input, outputPath, sheet, across, down, cancellationToken);
}
