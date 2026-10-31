using System.Security.Cryptography;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>What filtering did.</summary>
/// <param name="Changed">How many pictures were improved. With none, the output was not written.</param>
/// <param name="LeftAlone">Pictures it can't safely change: see-through ones, and scans already pure black and white.</param>
public sealed record PictureFilterResult(int Changed, int LeftAlone);

/// <summary>
/// Improves the pictures in a PDF, such as the page images of a scan. Text, links and form fields aren't
/// touched, because only the picture objects change. Works on a private copy and only a checked file reaches the output.
/// </summary>
internal static class PdfPictureFilter
{
    private const int SmallestPictureSide = 64;
    private const int OneBitPicture = 1;
    private const int JpegQuality = 88;

    public static async Task<PictureFilterResult> ApplyAsync(PdfiumThread thread, PdfInputFile input, PictureFilterOptions options, IReadOnlyCollection<int>? pages, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "filter", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string saved = Path.Combine(work, "pictures.pdf");
            await File.WriteAllBytesAsync(saved, [], cancellationToken).ConfigureAwait(false);
            RawPdf? doc = null;
            int changed = 0, leftAlone = 0, pageCount;
            try
            {
                pageCount = await thread.InvokeAsync(() =>
                {
                    doc = RawPdf.Load(input.Path, input.Password);
                    return doc.PageCount;
                }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

                int[] chosen = (pages ?? Enumerable.Range(0, pageCount)).Where(p => p >= 0 && p < pageCount).Distinct().Order().ToArray();

                // A picture on many pages only needs working out once, and stays one picture when the copies are merged.
                var uses = new Dictionary<string, int>();
                foreach (int index in chosen)
                {
                    await thread.InvokeAsync(() => PdfCompressor.CountPictures(doc!, index, uses, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                }

                var finished = new Dictionary<string, Finished>();
                for (int i = 0; i < chosen.Length; i++)
                {
                    int index = chosen[i];
                    var read = await thread.InvokeAsync(() => ReadPictures(doc!, index, input.Path, 1), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        leftAlone += read.LeftAlone;
                        var done = await Task.Run(() => read.Items.Select(item =>
                        {
                            if (finished.TryGetValue(item.Hash, out var known))
                            {
                                return (item.Object, known);
                            }

                            var made = Make(item.Pixels, options);
                            if (uses.GetValueOrDefault(item.Hash) > 1)
                            {
                                finished[item.Hash] = made;
                            }

                            return (item.Object, made);
                        }).ToArray(), cancellationToken).ConfigureAwait(false);

                        await thread.InvokeAsync(() => Replace(read.Page, done, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                        changed += done.Length;
                    }
                    finally
                    {
                        await thread.InvokeAsync(() => PdfiumNative.FPDF_ClosePage(read.Page), PdfWorkPriority.Background, CancellationToken.None).ConfigureAwait(false);
                    }

                    progress?.Report((i + 1.0) / chosen.Length);
                }

                if (changed > 0)
                {
                    await thread.InvokeAsync(() => PdfComposer.Save(doc!, saved, saved), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            }

            if (changed == 0)
            {
                return new PictureFilterResult(0, leftAlone);
            }

            string final = Path.Combine(work, "final.pdf");
            string merged = await PdfDeduper.MergeCopiesAsync(thread, saved, input.Password, work, cancellationToken).ConfigureAwait(false);
            await QpdfTool.RunAsync(QpdfTool.Tight(QpdfTool.Job(merged, final, input.Password)), input.Path, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, input.Password, pageCount, outputPath, cancellationToken).ConfigureAwait(false);
            return new PictureFilterResult(changed, leftAlone);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    /// <summary>
    /// Shows one page as the filter would leave it, without saving anything. Pictures are first scaled
    /// to the preview's size, which is what makes the sliders quick even on a large scan.
    /// </summary>
    public static async Task<PdfBitmap> PreviewAsync(PdfiumThread thread, PdfInputFile input, int pageIndex, PictureFilterOptions options, int width, int height, CancellationToken cancellationToken)
    {
        RawPdf? doc = null;
        nint page = 0;
        try
        {
            var read = await thread.InvokeAsync(() =>
            {
                doc = RawPdf.Load(input.Path, input.Password);
                var found = ReadPictures(doc, pageIndex, input.Path, Math.Max(width, height) * 2);
                page = found.Page;
                return found;
            }, PdfWorkPriority.Visible, cancellationToken).ConfigureAwait(false);

            var done = await Task.Run(() => read.Items.Select(item => (item.Object, Made: Make(item.Pixels, options))).ToArray(), cancellationToken).ConfigureAwait(false);
            return await thread.InvokeAsync(() =>
            {
                foreach (var (obj, made) in done)
                {
                    Put(obj, made);
                }

                return Render(page, width, height);
            }, PdfWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await thread.InvokeAsync(() =>
            {
                if (page != 0)
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }

                doc?.Dispose();
            }, PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static unsafe PdfBitmap Render(nint page, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
        {
            nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(width, height, PdfiumNative.FPDFBitmap_BGRA, (nint)p, width * 4);
            if (bitmap == 0)
            {
                throw new InvalidOperationException("Could not make a preview bitmap.");
            }

            try
            {
                _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, PdfiumNative.FPDF_ANNOT);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    /// <summary>The page's pictures that can be changed. The page stays open for the caller to close.</summary>
    private static unsafe Read ReadPictures(RawPdf doc, int index, string path, int previewSide)
    {
        nint page = PdfCompressor.LoadPage(doc, index, path);
        var items = new List<Picture>();
        int leftAlone = 0;
        int count = PdfiumNative.FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
            if (obj == 0 || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
            {
                continue;
            }

            PdfiumNative.FPDF_IMAGEOBJ_METADATA info;
            if (PdfiumNative.FPDFImageObj_GetImageMetadata(obj, page, &info) == 0
                || info.width < SmallestPictureSide || info.height < SmallestPictureSide)
            {
                continue; // icons and rules: too small to gain anything
            }

            if (info.bits_per_pixel <= OneBitPicture || PdfCompressor.IsSeeThrough(doc.Handle, page, obj))
            {
                leftAlone++;
                continue;
            }

            nint bitmap = PdfiumNative.FPDFImageObj_GetBitmap(obj);
            if (bitmap == 0)
            {
                leftAlone++;
                continue;
            }

            try
            {
                if (PdfDocument.ToBgra(bitmap) is not { } picture)
                {
                    leftAlone++;
                    continue;
                }

                // Only a real run counts sharing; a preview scales down instead.
                string hash = previewSide == 1 ? Convert.ToHexString(SHA256.HashData(PdfCompressor.RawData(obj))) : string.Empty;
                double scale = previewSide > 1 ? Math.Min(1, (double)previewSide / Math.Max(picture.Width, picture.Height)) : 1;
                items.Add(new Picture(obj, hash, scale < 1 ? PdfCompressor.Shrunk(picture, scale) : picture));
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }

        return new Read(page, items, leftAlone);
    }

    /// <summary>Black and white stays lossless, because JPEG leaves a haze around sharp letters. Everything else is a good JPEG.</summary>
    private static Finished Make(PdfBitmap picture, PictureFilterOptions options)
    {
        var filtered = PictureFilter.Apply(picture, options);
        return options.Look == PictureLook.BlackAndWhite
            ? new Finished(null, filtered)
            : new Finished(JpegEncoder.Encode(filtered, JpegQuality, options.Look == PictureLook.Grey), null);
    }

    private static int Replace(nint page, (nint Object, Finished Made)[] done, string path)
    {
        bool changed = false;
        foreach (var (obj, made) in done)
        {
            changed |= Put(obj, made);
        }

        if (changed && PdfiumNative.FPDFPage_GenerateContent(page) == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        return 0;
    }

    private static unsafe bool Put(nint obj, Finished made)
    {
        if (made.Jpeg != null)
        {
            return PdfCompressor.LoadJpeg(obj, made.Jpeg);
        }

        var picture = made.Lossless!;
        nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(picture.Width, picture.Height, PdfiumNative.FPDFBitmap_Gray, 0, 0);
        if (bitmap == 0)
        {
            return false;
        }

        try
        {
            int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            byte* to = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
            for (int y = 0; y < picture.Height; y++)
            {
                for (int x = 0; x < picture.Width; x++)
                {
                    to[(y * stride) + x] = picture.Pixels[(y * picture.Stride) + (x * 4)];
                }
            }

            nint noPages = 0;
            return PdfiumNative.FPDFImageObj_SetBitmap(&noPages, 0, obj, bitmap) != 0;
        }
        finally
        {
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
        }
    }

    private sealed record Picture(nint Object, string Hash, PdfBitmap Pixels);

    private sealed record Finished(byte[]? Jpeg, PdfBitmap? Lossless);

    private sealed record Read(nint Page, List<Picture> Items, int LeftAlone);
}
