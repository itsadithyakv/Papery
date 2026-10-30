using Papery.Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>A box on a page whose contents must really go.</summary>
public sealed record RedactionArea(int PageIndex, NormalizedRect Area);

/// <summary>
/// Removes content for good. Nothing clever is attempted on the original content, because a mistake here
/// exposes someone's private data. Each affected page is drawn to a picture with solid black boxes burned in, every original object
/// on it is deleted, and the words outside the boxes go back as an invisible layer so the rest stays searchable.
/// Everything the page's old content could be reached through is then dropped, and the result is checked.
/// </summary>
internal static class PdfRedactor
{
    private const double PictureDpi = 220;
    private const int PictureQuality = 85;
    private const int LongestSide = 7000;

    /// <param name="alsoFlatten">Pages to turn into pictures with no boxes at all (for removing hidden layers).</param>
    public static async Task ApplyAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<RedactionArea> areas, IReadOnlyList<int> alsoFlatten, string outputPath, CancellationToken cancellationToken)
    {
        if (input.Password != null)
        {
            // A password would have to be carried through steps that can't keep its permissions.
            throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
        }

        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "redact", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = input.Path;
            bool hasForm = await thread.InvokeAsync(() =>
            {
                using var doc = RawPdf.Load(input.Path, null);
                return PdfiumNative.FPDF_GetFormType(doc.Handle) != 0;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            // Form answers are stored outside the pages, where page-level removal wouldn't reach them.
            if (hasForm)
            {
                string flat = Path.Combine(work, "flat.pdf");
                var job = QpdfTool.Job(source, flat, null);
                job["generateAppearances"] = string.Empty;
                job["flattenAnnotations"] = "all";
                await QpdfTool.RunAsync(job, input.Path, cancellationToken).ConfigureAwait(false);
                source = flat;
            }

            var byPage = areas.GroupBy(a => a.PageIndex).ToDictionary(g => g.Key, g => g.Select(a => a.Area).ToArray());
            var pages = byPage.Keys.Concat(alsoFlatten).Distinct().Order().ToArray();

            string redacted = Path.Combine(work, "redacted.pdf");
            await File.WriteAllBytesAsync(redacted, [], cancellationToken).ConfigureAwait(false);
            RawPdf? doc = null;
            int pageCount;
            try
            {
                pageCount = await thread.InvokeAsync(() =>
                {
                    doc = RawPdf.Load(source, null);
                    return doc.PageCount;
                }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

                foreach (int index in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NormalizedRect[] boxes = byPage.GetValueOrDefault(index) ?? [];
                    var (bitmap, keep) = await thread.InvokeAsync(() => Read(doc!, index, boxes, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                    byte[] jpeg = await Task.Run(() => JpegEncoder.Encode(bitmap, PictureQuality, grey: false), cancellationToken).ConfigureAwait(false);
                    await thread.InvokeAsync(() => Rebuild(doc!, index, jpeg, keep, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                }

                await thread.InvokeAsync(() => PdfComposer.Save(doc!, redacted, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            }

            string scrubbed = Path.Combine(work, "scrubbed.pdf");
            await Task.Run(() => Scrub(redacted, scrubbed, pages), cancellationToken).ConfigureAwait(false);

            // QPDF only writes what's still reachable, so everything cut loose above is gone from the file.
            string final = Path.Combine(work, "final.pdf");
            await QpdfTool.RunAsync(QpdfTool.Tight(QpdfTool.Job(scrubbed, final, null)), input.Path, cancellationToken).ConfigureAwait(false);

            await thread.InvokeAsync(() => CheckNothingRemains(final, byPage, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, null, pageCount, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    private static bool Touches(NormalizedRect a, NormalizedRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static unsafe (PdfBitmap Picture, List<OcrWord> Keep) Read(RawPdf doc, int index, NormalizedRect[] boxes, string path)
    {
        nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        try
        {
            nint textPage = PdfiumNative.FPDFText_LoadPage(page);
            var keep = new List<OcrWord>();
            if (textPage != 0)
            {
                keep.AddRange(PdfDocument.ReadWords(page, textPage).Where(w => !boxes.Any(b => Touches(w.Area, b))).Select(w => new OcrWord(w.Text, w.Area)));
                PdfiumNative.FPDFText_ClosePage(textPage);
            }

            var builder = new AnnotationObjects(doc.Handle, page, 0, new PdfColor(0, 0, 0));
            var (width, height) = builder.PageSize();
            double scale = Math.Min(PictureDpi / 72, LongestSide / Math.Max(width, height));
            int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
            byte[] pixels = new byte[w * h * 4];
            fixed (byte* p = pixels)
            {
                nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(w, h, PdfiumNative.FPDFBitmap_BGRA, (nint)p, w * 4);
                _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, PdfiumNative.FPDF_ANNOT);
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            // The pixels underneath are overwritten here, before the picture is ever encoded.
            foreach (var box in boxes)
            {
                int left = Math.Clamp((int)Math.Floor(box.X * w) - 1, 0, w), right = Math.Clamp((int)Math.Ceiling(box.Right * w) + 1, 0, w);
                int top = Math.Clamp((int)Math.Floor(box.Y * h) - 1, 0, h), bottom = Math.Clamp((int)Math.Ceiling(box.Bottom * h) + 1, 0, h);
                for (int y = top; y < bottom; y++)
                {
                    Array.Clear(pixels, ((y * w) + left) * 4, (right - left) * 4);
                    for (int x = left; x < right; x++)
                    {
                        pixels[(((y * w) + x) * 4) + 3] = 255;
                    }
                }
            }

            return (new PdfBitmap(w, h, w * 4, pixels), keep);
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static void Rebuild(RawPdf doc, int index, byte[] jpeg, List<OcrWord> keep, string path)
    {
        nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        try
        {
            for (int i = PdfiumNative.FPDFPage_CountObjects(page) - 1; i >= 0; i--)
            {
                nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
                if (PdfiumNative.FPDFPage_RemoveObject(page, obj) == 0)
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, path);
                }

                PdfiumNative.FPDFPageObj_Destroy(obj);
            }

            // Comments, links and the like are already drawn into the picture if they showed.
            for (int i = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
            {
                _ = PdfiumNative.FPDFPage_RemoveAnnot(page, i);
            }

            using var fonts = new FontBook();
            var builder = new AnnotationObjects(doc.Handle, page, 0, new PdfColor(0, 0, 0), fonts);
            if (!builder.PictureFromJpeg(jpeg, new NormalizedRect(0, 0, 1, 1)))
            {
                throw new PdfEditException(PdfEditError.BuildFailed, path);
            }

            foreach (var word in keep)
            {
                builder.InvisibleWord(word.Text, word.Area);
            }

            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, path);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>Other routes to a page's old words: tagged-text trees, thumbnails, and private app data.</summary>
    private static void Scrub(string input, string output, IReadOnlyList<int> pages)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (string key in new[] { "/StructTreeRoot", "/MarkInfo" })
        {
            doc.Internals.Catalog.Elements.Remove(key);
        }

        foreach (int index in pages)
        {
            var page = doc.Pages[index];
            foreach (string key in new[] { "/Thumb", "/PieceInfo", "/StructParents", "/B", "/AA", "/Metadata", "/Annots", "/LastModified" })
            {
                page.Elements.Remove(key);
            }
        }

        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        doc.Save(stream, closeStream: false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads the finished file afresh. If any word still sits in a box, nothing is saved.</summary>
    private static void CheckNothingRemains(string path, Dictionary<int, NormalizedRect[]> boxes, string outputPath)
    {
        using var doc = RawPdf.Load(path, null);
        foreach (var (index, areas) in boxes)
        {
            nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
            if (page == 0)
            {
                throw new PdfEditException(PdfEditError.VerifyFailed, outputPath);
            }

            try
            {
                nint textPage = PdfiumNative.FPDFText_LoadPage(page);
                if (textPage != 0)
                {
                    bool leaked = PdfDocument.ReadWords(page, textPage).Any(w => areas.Any(a => Touches(w.Area, Inset(a))));
                    PdfiumNative.FPDFText_ClosePage(textPage);
                    if (leaked)
                    {
                        throw new PdfEditException(PdfEditError.VerifyFailed, outputPath);
                    }
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>A word kept beside a box can round into its very edge when read back, which isn't a leak.</summary>
    private static NormalizedRect Inset(NormalizedRect a)
    {
        const double Edge = 0.002;
        return new NormalizedRect(a.X + Edge, a.Y + Edge, Math.Max(0, a.Width - (2 * Edge)), Math.Max(0, a.Height - (2 * Edge)));
    }
}
