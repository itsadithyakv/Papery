using Papery.Pdf.Editing;

namespace Papery.Pdf;

/// <summary>
/// Entry point to the PDF engine. There is exactly one per process, because
/// PDFium itself is a process-wide library with a single worker thread.
/// </summary>
#pragma warning disable CA1822 // Everything goes through PdfEngine.Shared, whether or not it needs the thread.
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
        Unlocked(input, outputPath, (file, output) => PdfFieldWriter.AddAsync(Thread, file, fields, output, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a form's answers to a CSV or XFDF file, one per field, by field name. Returns how many; with none
    /// (no form), no file is made.
    /// </summary>
    public async Task<int> ExportFormDataAsync(PdfInputFile input, string path, FormDataFormat format, CancellationToken cancellationToken = default)
    {
        await using var document = await OpenAsync(input.Path, input.Password, cancellationToken).ConfigureAwait(false);
        return await FormDataExchange.ExportAsync(document, path, format, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same from an open document, including answers not saved yet.</summary>
    public Task<int> ExportFormDataAsync(PdfDocument document, string path, FormDataFormat format, CancellationToken cancellationToken = default) =>
        FormDataExchange.ExportAsync(document, path, format, cancellationToken);

    /// <summary>
    /// Fills a form from a CSV or XFDF file (Papery's or Acrobat's) by field name, then saves through the checked save.
    /// When nothing could be filled, the output isn't written. Throws InvalidDataException for a file that is neither.
    /// </summary>
    public Task<FormImportResult> ImportFormDataAsync(PdfInputFile input, string path, string outputPath, CancellationToken cancellationToken = default) =>
        FormDataExchange.ImportAsync(this, input, path, outputPath, cancellationToken);

    /// <summary>Makes form answers and annotations a permanent part of the pages.</summary>
    public Task FlattenAsync(PdfInputFile input, string outputPath, CancellationToken cancellationToken = default) =>
        PdfComposer.FlattenAsync(Thread, input, outputPath, Native.PdfiumNative.FLAT_NORMALDISPLAY, cancellationToken);

    /// <summary>Writes a Word document from an open PDF. It's a best effort at rebuilding paragraphs, not a perfect copy of the layout.</summary>
    /// <summary>Attaches files to the PDF and removes the attachments at <paramref name="remove"/> (indexes as listed by the document).</summary>
    public Task ChangeAttachmentsAsync(PdfInputFile input, IReadOnlyList<string> add, IReadOnlyList<int> remove, string outputPath, CancellationToken cancellationToken = default) =>
        Unlocked(input, outputPath, (file, output) => PdfAttacher.ChangeAsync(Thread, file, add, remove, output, cancellationToken), cancellationToken);

    /// <summary>Finds words in every PDF in a folder and its subfolders. Results arrive as each file is read.</summary>
    public IAsyncEnumerable<FolderSearchHit> SearchFolderAsync(string folder, string query, IProgress<FolderSearchProgress>? progress = null, CancellationToken cancellationToken = default) =>
        PdfFolderSearch.SearchAsync(this, folder, query, progress, cancellationToken);

    public Task ExportWordAsync(PdfDocument document, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        DocxWriter.ExportAsync(document, outputPath, progress, cancellationToken);

    /// <summary>
    /// Writes a PowerPoint file from an open PDF, one slide per page, shaped like the first page. Text comes as boxes that can be
    /// edited and pictures as pictures, near where they were; a page with no text to read, like a scan, comes as one picture.
    /// </summary>
    public Task ExportPowerPointAsync(PdfDocument document, string outputPath, PowerPointOptions? options = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        PptxWriter.ExportAsync(document, outputPath, options ?? new PowerPointOptions(), progress, cancellationToken);

    /// <summary>Adds a certified digital signature using a certificate.</summary>
    public Task SignAsync(PdfInputFile input, PdfSignatureRequest request, string outputPath, CancellationToken cancellationToken = default) =>
        PdfSigner.SignAsync(Thread, input, request, outputPath, cancellationToken);

    /// <summary>Certificates in the Windows store that can sign.</summary>
    public IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> SigningCertificates() => PdfSigner.UsableCertificates();

    /// <summary>Permanently removes everything in the boxes, then checks that it's gone. PDFs with a password need it taken off first.</summary>
    public Task RedactAsync(PdfInputFile input, IReadOnlyList<RedactionArea> areas, string outputPath, CancellationToken cancellationToken = default) =>
        PdfRedactor.ApplyAsync(Thread, input, areas, clearFileLevel: true, outputPath, cancellationToken);

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
        Unlocked(input, outputPath, (file, output) => PdfSecurity.SetMetadataAsync(Thread, file, metadata, output, cancellationToken), cancellationToken);

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
        PdfCompressor.FitUnderAsync(Thread, input, limitBytes, fastWebView: false, outputPath, cancellationToken);

    /// <summary>The same, laid out for fast web view as well.</summary>
    public Task<CompressResult> CompressToFitAsync(PdfInputFile input, long limitBytes, bool fastWebView, string outputPath, CancellationToken cancellationToken = default) =>
        PdfCompressor.FitUnderAsync(Thread, input, limitBytes, fastWebView, outputPath, cancellationToken);

    /// <summary>
    /// Makes a PDF from lines of plain text already wrapped to <see cref="TextPageOptions.Columns"/>, in a typewriter
    /// font. A line holding just "" starts a new page. Returns the number of pages.
    /// </summary>
    public Task<int> TextToPdfAsync(IReadOnlyList<string> lines, TextPageOptions options, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        TextToPdfWriter.WriteAsync(Thread, lines, options, outputPath, progress, cancellationToken);

    /// <summary>Suggested bookmarks from the headings of an open document (lines set larger than the body text).</summary>
    public async Task<IReadOnlyList<PdfOutlineItem>> FindHeadingsAsync(PdfDocument document, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var pages = new List<IReadOnlyList<PdfWord>>(document.PageCount);
        for (int page = 0; page < document.PageCount; page++)
        {
            pages.Add(await document.GetPageWordsAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false));
            progress?.Report((page + 1.0) / document.PageCount);
        }

        return HeadingOutline.Find(pages);
    }

    /// <summary>Saves the text as a spreadsheet, keeping tables in columns. Returns the rows written; with none, no file is made.</summary>
    public Task<int> ExportExcelAsync(PdfDocument document, bool oneSheet, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        XlsxWriter.ExportAsync(document, oneSheet, outputPath, progress, cancellationToken);

    /// <summary>Checks the file's digital signatures, offline.</summary>
    public Task<IReadOnlyList<PdfSignatureCheck>> CheckSignaturesAsync(PdfInputFile input, CancellationToken cancellationToken = default) =>
        PdfSignatureChecker.CheckAsync(Thread, input, cancellationToken);

    /// <summary>
    /// Tries to change a line of text inside the page's own content. False means it couldn't be done safely and nothing
    /// was written, usually because the font doesn't hold the letters wanted.
    /// </summary>
    public Task<bool> TryReplaceLineAsync(PdfInputFile input, int page, NormalizedRect line, string oldText, string newText, string outputPath, CancellationToken cancellationToken = default) =>
        PdfTextReplacer.TryAsync(Thread, input, page, line, oldText, newText, outputPath, cancellationToken);

    /// <summary>Improves the pictures in a PDF. With nothing to change, the output isn't written and the result says so.</summary>
    /// <param name="pages">Which pages, or null for all of them.</param>
    public Task<PictureFilterResult> FilterPicturesAsync(PdfInputFile input, PictureFilterOptions options, IReadOnlyCollection<int>? pages, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        PdfPictureFilter.ApplyAsync(Thread, input, options, pages, outputPath, progress, cancellationToken);

    /// <summary>Draws one page as the filter would leave it. Nothing is saved.</summary>
    public Task<PdfBitmap> PreviewPictureFilterAsync(PdfInputFile input, int page, PictureFilterOptions options, int width, int height, CancellationToken cancellationToken = default) =>
        PdfPictureFilter.PreviewAsync(Thread, input, page, options, width, height, cancellationToken);

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
        Unlocked(input, outputPath, (file, output) => PdfLinkWriter.ApplyAsync(Thread, file, remove, add, output, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes every comment and markup to an XFDF file, the format Acrobat swaps comments in. Links, form fields and
    /// placed pictures aren't comments and stay out. Returns how many; with none, no file is made.
    /// </summary>
    public async Task<int> ExportCommentsAsync(PdfInputFile input, string xfdfPath, CancellationToken cancellationToken = default)
    {
        await using var document = await OpenAsync(input.Path, input.Password, cancellationToken).ConfigureAwait(false);
        return await CommentExchange.ExportAsync(document, xfdfPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same from an open document, including comments not saved yet.</summary>
    public Task<int> ExportCommentsAsync(PdfDocument document, string xfdfPath, CancellationToken cancellationToken = default) =>
        CommentExchange.ExportAsync(document, xfdfPath, cancellationToken);

    /// <summary>
    /// Adds the comments in an XFDF file (Papery's or Acrobat's) to a PDF, as Papery annotations that stay editable,
    /// then saves through the checked save. When nothing is added, the output isn't written. Throws
    /// InvalidDataException for a file that isn't XFDF, including any with a DTD.
    /// </summary>
    public Task<CommentImportResult> ImportCommentsAsync(PdfInputFile input, string xfdfPath, string outputPath, CancellationToken cancellationToken = default) =>
        CommentExchange.ImportAsync(this, input, xfdfPath, outputPath, cancellationToken);

    /// <summary>Replaces a saved PDF's bookmarks. An empty list removes them all.</summary>
    public Task SetOutlineAsync(PdfInputFile input, IReadOnlyList<PdfOutlineItem> outline, string outputPath, CancellationToken cancellationToken = default) =>
        Unlocked(input, outputPath, (file, output) => PdfLinkWriter.SetOutlineAsync(Thread, file, outline, output, cancellationToken), cancellationToken);

    /// <summary>PDFsharp won't change a locked file without its owner password, which Papery's own locked files keep random.</summary>
    private Task Unlocked(PdfInputFile input, string outputPath, Func<PdfInputFile, string, Task> change, CancellationToken cancellationToken) =>
        input.Password == null ? change(input, outputPath) : PdfRedactor.KeepingPasswordAsync(Thread, input, outputPath, change, cancellationToken);

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
