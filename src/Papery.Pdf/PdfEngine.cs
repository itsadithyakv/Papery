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

    /// <summary>Permanently removes everything in the boxes, then checks that it's gone. PDFs with a password need it taken off first.</summary>
    public Task RedactAsync(PdfInputFile input, IReadOnlyList<RedactionArea> areas, string outputPath, CancellationToken cancellationToken = default) =>
        PdfRedactor.ApplyAsync(Thread, input, areas, [], outputPath, cancellationToken);

    /// <summary>Locks a PDF with a password (AES-256) and optional limits.</summary>
    public Task ProtectAsync(PdfInputFile input, PdfProtection protection, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSecurity.ProtectAsync(Thread, input, protection, outputPath, cancellationToken);

    /// <summary>Takes a password off, given the password.</summary>
    public Task RemovePasswordAsync(PdfInputFile input, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSecurity.RemovePasswordAsync(Thread, input, outputPath, cancellationToken);

    /// <summary>Rebuilds a damaged PDF into a new file. Throws if nothing readable can be saved.</summary>
    public Task RepairAsync(string inputPath, string? password, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSecurity.RepairAsync(Thread, inputPath, password, outputPath, cancellationToken);

    /// <summary>Changes the title, author, subject and keywords.</summary>
    public Task SetMetadataAsync(PdfInputFile input, PdfDescription metadata, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSecurity.SetMetadataAsync(Thread, input, metadata, outputPath, cancellationToken);

    /// <summary>Looks for metadata, comments, attachments, hidden layers and scripts.</summary>
    public Task<PdfHiddenData> InspectHiddenDataAsync(PdfInputFile input, CancellationToken cancellationToken = default) =>
        PdfSecurity.InspectAsync(input, cancellationToken);

    /// <summary>Takes the chosen hidden things out of the file for good.</summary>
    public Task RemoveHiddenDataAsync(PdfInputFile input, PdfHiddenDataChoice choice, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSecurity.RemoveHiddenDataAsync(Thread, input, choice, outputPath, cancellationToken);

    /// <summary>Makes a smaller copy. If nothing smaller is possible, the output isn't written and the result says so.</summary>
    public Task<CompressResult> CompressAsync(PdfInputFile input, CompressOptions options, string outputPath, CancellationToken cancellationToken = default) =>
        PdfCompressor.CompressAsync(Thread, input, options, outputPath, cancellationToken);

    /// <summary>Compresses no harder than it must to fit under a size. If it can't fit, it gives the smallest it could manage.</summary>
    public Task<CompressResult> CompressToFitAsync(PdfInputFile input, long limitBytes, string outputPath, CancellationToken cancellationToken = default) =>
        PdfCompressor.FitUnderAsync(Thread, input, limitBytes, outputPath, cancellationToken);

    /// <summary>Adds recognised words to a saved PDF as an invisible layer, so it can be searched and copied.</summary>
    public Task AddTextLayerAsync(PdfInputFile input, IReadOnlyList<OcrPage> pages, string outputPath, CancellationToken cancellationToken = default) =>
        OcrTextWriter.ApplyAsync(Thread, input, pages, outputPath, cancellationToken);

    /// <summary>Makes a new PDF with one page for each picture.</summary>
    public Task ImagesToPdfAsync(IReadOnlyList<PdfImagePage> pages, ImagePageOptions options, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        ImageToPdfWriter.WriteAsync(Thread, pages, options, outputPath, progress, cancellationToken);

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
