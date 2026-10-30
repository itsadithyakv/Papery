using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>What a compose produced.</summary>
/// <param name="BookmarksSkipped">True if the pages were saved but bookmarks/title could not be added.</param>
public sealed record PdfComposeResult(string Path, int PageCount, bool BookmarksSkipped);

/// <summary>
/// Builds a PDF from a <see cref="PdfComposeRequest"/> and saves it safely.
/// </summary>
/// <remarks>
/// PDFium does the page work (it copes with damaged and encrypted inputs far better than
/// any managed library); PDFsharp then adds bookmarks to brand-new documents, which PDFium
/// has no API for. Each step is its own job on the PDFium thread at background priority,
/// so the viewer keeps drawing pages while a big merge runs.
/// </remarks>
internal static class PdfComposer
{
    // Reading every page back is the strongest check; past this many pages, check a spread sample.
    private const int VerifyAllPagesUpTo = 400;

    public static async Task<PdfComposeResult> ComposeAsync(
        PdfiumThread thread,
        PdfComposeRequest request,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(request.Pages.Count, nameof(request));
        outputPath = Path.GetFullPath(outputPath);
        var pages = request.Pages;
        var opened = new RawPdf?[request.Sources.Count];
        RawPdf? dest = null;
        string? inPlacePassword = request.EditInPlaceOf is int b ? request.Sources[b].Password : null;
        var temps = new List<string>();
        const PdfWorkPriority Priority = PdfWorkPriority.Background;

        RawPdf Source(int index)
        {
            if (opened[index] is { } doc)
            {
                return doc;
            }

            var input = request.Sources[index];
            doc = opened[index] = RawPdf.Load(input.Path, input.Password);
            if (!doc.CanAssemble)
            {
                throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
            }

            return doc;
        }

        try
        {
            progress?.Report(0.02);

            // 1. Open the inputs (checking pages exist and changes are allowed) and the starting document.
            var plan = await thread.InvokeAsync(() =>
            {
                foreach (var page in pages.Where(p => !p.IsBlank))
                {
                    int count = Source(page.Source).PageCount;
                    if ((uint)page.Page >= (uint)count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(request), $"Page {page.Page + 1} does not exist in '{request.Sources[page.Source].Path}'.");
                    }
                }

                if (request.EditInPlaceOf is int baseIndex)
                {
                    // A second handle on the same file: this one gets modified, the first stays a clean import source.
                    var input = request.Sources[baseIndex];
                    dest = RawPdf.Load(input.Path, input.Password);
                    if (!dest.CanAssemble)
                    {
                        throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
                    }
                }
                else
                {
                    dest = RawPdf.CreateNew();
                }

                return ComposePlan.Create(pages, request.EditInPlaceOf, dest.PageCount);
            }, Priority, cancellationToken).ConfigureAwait(false);

            progress?.Report(0.05);

            // 2. Copy pages in, one source at a time.
            int done = 0, total = Math.Max(1, plan.Imports.Sum(i => i.Pages.Length));
            foreach (var import in plan.Imports)
            {
                await thread.InvokeAsync(() =>
                {
                    var source = Source(import.Source);
                    if (!ImportPages(dest!, source, import.Pages))
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, source.FilePath);
                    }
                }, Priority, cancellationToken).ConfigureAwait(false);

                done += import.Pages.Length;
                progress?.Report(0.05 + (0.5 * done / total));
            }

            // 3. Blank pages, deletions, order, rotation.
            await thread.InvokeAsync(() =>
            {
                nint doc = dest!.Handle;
                foreach (var size in plan.Blanks)
                {
                    nint page = PdfiumNative.FPDFPage_New(doc, dest.PageCount, size.Width, size.Height);
                    if (page == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
                    }

                    PdfiumNative.FPDF_ClosePage(page);
                }

                foreach (int index in plan.Deletions)
                {
                    PdfiumNative.FPDFPage_Delete(doc, index);
                }

                if (plan.Order is { } order && !MovePages(doc, order))
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
                }

                for (int k = 0; k < pages.Count; k++)
                {
                    bool reshape = pages[k].Geometry is { IsEmpty: false };
                    if (pages[k].QuarterTurns == 0 && !reshape)
                    {
                        continue;
                    }

                    nint page = PdfiumNative.FPDF_LoadPage(doc, k);
                    if (page == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
                    }

                    PdfiumNative.FPDFPage_SetRotation(page, (PdfiumNative.FPDFPage_GetRotation(page) + pages[k].QuarterTurns) & 3);
                    if (reshape)
                    {
                        // After the turn, so "crop the top" means the top as the user now sees it.
                        PageGeometryApplier.Apply(page, pages[k].Geometry!);
                    }

                    PdfiumNative.FPDF_ClosePage(page);
                }

                if (request.EditInPlaceOf is null)
                {
                    var first = pages.FirstOrDefault(p => !p.IsBlank);
                    if (!first.IsBlank && opened[first.Source] is { } source)
                    {
                        _ = PdfiumNative.FPDF_CopyViewerPreferences(doc, source.Handle);
                    }
                }

                if (dest.PageCount != pages.Count)
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
                }
            }, Priority, cancellationToken).ConfigureAwait(false);

            progress?.Report(0.6);

            // 4. Write it out. A full rewrite (not an incremental update), so removed pages are not
            //    left hidden in the file. An edited file keeps its password and permissions.
            string temp = SafeFile.CreateTemp(outputPath);
            temps.Add(temp);
            await thread.InvokeAsync(() => Save(dest!, temp, outputPath), Priority, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.8);

            // 5. Read it back before it replaces anything.
            await thread.InvokeAsync(() => Verify(temp, inPlacePassword, pages.Count, outputPath), Priority, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.9);

            // Inputs must be closed before the result can replace one of them.
            await thread.InvokeAsync(() => CloseAll(opened, ref dest), Priority, CancellationToken.None).ConfigureAwait(false);

            // 6. Bookmarks and title for new documents.
            bool bookmarksSkipped = false;
            string final = temp;
            if (request.EditInPlaceOf is null && (request.Outline is { Count: > 0 } || request.Title != null))
            {
                string withOutline = SafeFile.CreateTemp(outputPath);
                temps.Add(withOutline);
                try
                {
                    await Task.Run(() => PdfOutlineWriter.Write(temp, withOutline, request.Outline, request.Title), cancellationToken).ConfigureAwait(false);
                    await thread.InvokeAsync(() => Verify(withOutline, null, pages.Count, outputPath), Priority, cancellationToken).ConfigureAwait(false);
                    final = withOutline;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    bookmarksSkipped = true; // the pages are fine; save them without bookmarks
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(final, outputPath);
            progress?.Report(1);
            return new PdfComposeResult(outputPath, pages.Count, bookmarksSkipped);
        }
        finally
        {
            await thread.InvokeAsync(() => CloseAll(opened, ref dest), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            foreach (string temp in temps)
            {
                SafeFile.TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// Several pages on each sheet (2-up, 4-up) as a new PDF, for printing. PDFium draws each page
    /// as artwork on the sheet, so links, comments and form fields are not carried over.
    /// </summary>
    public static async Task<PdfComposeResult> NUpAsync(
        PdfiumThread thread,
        PdfInputFile input,
        string outputPath,
        PdfPageSize sheet,
        int across,
        int down,
        CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        RawPdf? source = null, result = null;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            int sheets = await thread.InvokeAsync(() =>
            {
                source = RawPdf.Load(input.Path, input.Password);
                if (!source.CanAssemble)
                {
                    throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
                }

                nint handle = PdfiumNative.FPDF_ImportNPagesToOne(source.Handle, sheet.Width, sheet.Height, (nuint)across, (nuint)down);
                if (handle == 0)
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                }

                result = RawPdf.Wrap(handle);
                return result.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            await thread.InvokeAsync(() => Save(result!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => Verify(temp, null, sheets, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => { result?.Dispose(); source?.Dispose(); result = source = null; }, PdfWorkPriority.Background, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
            return new PdfComposeResult(outputPath, sheets, BookmarksSkipped: false);
        }
        finally
        {
            await thread.InvokeAsync(() => { result?.Dispose(); source?.Dispose(); }, PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>
    /// Burns form answers and annotations into the page content: they can't be changed any more, and
    /// look the same everywhere. Always writes through the checked save, so a failure changes nothing.
    /// </summary>
    public static async Task FlattenAsync(PdfiumThread thread, PdfInputFile input, string outputPath, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        RawPdf? doc = null;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            int pages = await thread.InvokeAsync(() =>
            {
                doc = RawPdf.Load(input.Path, input.Password);
                int count = doc.PageCount;
                for (int i = 0; i < count; i++)
                {
                    nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, i);
                    if (page == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                    }

                    _ = PdfiumNative.FPDFPage_Flatten(page, PdfiumNative.FLAT_NORMALDISPLAY);
                    PdfiumNative.FPDF_ClosePage(page);
                }

                return count;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            await thread.InvokeAsync(() => Save(doc!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => Verify(temp, input.Password, pages, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => { doc?.Dispose(); doc = null; }, PdfWorkPriority.Background, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            SafeFile.TryDelete(temp);
        }
    }

    private static unsafe bool ImportPages(RawPdf dest, RawPdf source, int[] pages)
    {
        fixed (int* indices = pages)
        {
            return PdfiumNative.FPDF_ImportPagesByIndex(dest.Handle, source.Handle, indices, (uint)pages.Length, dest.PageCount) != 0;
        }
    }

    private static unsafe bool MovePages(nint doc, int[] order)
    {
        fixed (int* indices = order)
        {
            return PdfiumNative.FPDF_MovePages(doc, indices, (uint)order.Length, 0) != 0;
        }
    }

    internal static unsafe void Save(RawPdf doc, string tempPath, string outputPath)
    {
        try
        {
            // Open (not Create): the temp file is hidden, and Create refuses to overwrite hidden files.
            using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
            stream.SetLength(0);
            using var writer = new PdfFileWriter(stream);
            bool ok = PdfiumNative.FPDF_SaveAsCopy(doc.Handle, writer.Native, PdfiumNative.FPDF_NO_INCREMENTAL) != 0;
            if (writer.Error != null)
            {
                throw new PdfEditException(PdfEditError.CannotWrite, outputPath, writer.Error);
            }

            if (!ok)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
            }

            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEditException(PdfEditError.CannotWrite, outputPath, ex);
        }
    }

    /// <summary>Opens the written file fresh and loads its pages; throws VerifyFailed if anything is off.</summary>
    internal static void Verify(string path, string? password, int expectedPages, string outputPath)
    {
        try
        {
            using var doc = RawPdf.Load(path, password);
            int count = doc.PageCount;
            if (count != expectedPages)
            {
                throw new PdfEditException(PdfEditError.VerifyFailed, outputPath);
            }

            int step = count <= VerifyAllPagesUpTo ? 1 : (count / VerifyAllPagesUpTo) + 1;
            for (int i = 0; i < count; i += step)
            {
                LoadAndClose(doc.Handle, i, outputPath);
            }

            LoadAndClose(doc.Handle, count - 1, outputPath);
        }
        catch (PdfOpenException ex)
        {
            throw new PdfEditException(PdfEditError.VerifyFailed, outputPath, ex);
        }
    }

    private static void LoadAndClose(nint doc, int index, string outputPath)
    {
        nint page = PdfiumNative.FPDF_LoadPage(doc, index);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.VerifyFailed, outputPath);
        }

        PdfiumNative.FPDF_ClosePage(page);
    }

    private static bool CloseAll(RawPdf?[] opened, ref RawPdf? dest)
    {
        dest?.Dispose();
        dest = null;
        for (int i = 0; i < opened.Length; i++)
        {
            opened[i]?.Dispose();
            opened[i] = null;
        }

        return true;
    }
}
