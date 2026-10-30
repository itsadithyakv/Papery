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
