using System.Security.Cryptography;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>How hard to squeeze pictures. Text and shapes are never made worse.</summary>
/// <param name="MaxDpi">Pictures sharper than this on the page are scaled down to it.</param>
/// <param name="JpegQuality">1 (smallest) to 100 (best).</param>
/// <param name="Greyscale">Turn pictures grey. Text and shapes keep their colours.</param>
public sealed record CompressOptions(int MaxDpi, int JpegQuality, bool Greyscale = false)
{
    public static CompressOptions HighQuality { get; } = new(220, 85);

    public static CompressOptions Balanced { get; } = new(150, 72);

    public static CompressOptions Smallest { get; } = new(96, 50);

    /// <summary>Tried in turn to fit under a size limit, gentlest first.</summary>
    internal static IReadOnlyList<CompressOptions> Ladder { get; } =
    [
        HighQuality, Balanced, new(120, 60), Smallest, new(72, 35),
    ];
}

/// <summary>What compressing did.</summary>
/// <param name="Changed">False when nothing smaller could be made; the output was then not written.</param>
/// <param name="FitsLimit">Whether it came in under the size asked for (true when none was asked for).</param>
public sealed record CompressResult(long OriginalBytes, long NewBytes, bool Changed, bool FitsLimit = true);

/// <summary>
/// Makes PDFs smaller: first PDFium shrinks pictures, then QPDF rewrites the file tightly and drops what
/// nothing uses. Works on private copies, and only a smaller, checked result ever reaches the output.
/// </summary>
internal static class PdfCompressor
{
    private const int SmallestPictureSide = 64;
    private const int OneBitPicture = 1;

    // Not worth a generation of JPEG loss for less than this.
    private const double WorthwhileSaving = 0.9;

    public static async Task<CompressResult> CompressAsync(PdfiumThread thread, PdfInputFile input, CompressOptions options, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWorkFolder();
        try
        {
            long original = new FileInfo(input.Path).Length;
            string built = await BuildAsync(thread, input, options, work, 0, cancellationToken).ConfigureAwait(false);
            long size = new FileInfo(built).Length;
            if (size >= original)
            {
                return new CompressResult(original, original, false);
            }

            await CommitAsync(thread, input, built, outputPath, cancellationToken).ConfigureAwait(false);
            return new CompressResult(original, size, true);
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    /// <summary>The gentlest setting that fits under the limit, or the smallest result it could manage.</summary>
    public static async Task<CompressResult> FitUnderAsync(PdfiumThread thread, PdfInputFile input, long limitBytes, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWorkFolder();
        try
        {
            long original = new FileInfo(input.Path).Length;
            string? best = null;
            long bestSize = original;
            int attempt = 0;
            foreach (var options in CompressOptions.Ladder)
            {
                string built = await BuildAsync(thread, input, options, work, attempt++, cancellationToken).ConfigureAwait(false);
                long size = new FileInfo(built).Length;
                if (size < bestSize)
                {
                    best = built;
                    bestSize = size;
                }

                if (size <= limitBytes)
                {
                    break;
                }
            }

            if (best == null)
            {
                return new CompressResult(original, original, false, original <= limitBytes);
            }

            await CommitAsync(thread, input, best, outputPath, cancellationToken).ConfigureAwait(false);
            return new CompressResult(original, bestSize, true, bestSize <= limitBytes);
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    private static async Task CommitAsync(PdfiumThread thread, PdfInputFile input, string built, string outputPath, CancellationToken cancellationToken)
    {
        int pages = await thread.InvokeAsync(() =>
        {
            using var doc = RawPdf.Load(input.Path, input.Password);
            return doc.PageCount;
        }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        await CommitAsync(thread, built, input.Password, pages, outputPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks a finished private copy, then swaps it into place.</summary>
    internal static async Task CommitAsync(PdfiumThread thread, string built, string? password, int pages, string outputPath, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await using (var from = new FileStream(built, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var to = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                to.SetLength(0);
                await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
            }

            await thread.InvokeAsync(() => PdfComposer.Verify(temp, password, pages, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>Shrinks a private copy and returns its path.</summary>
    private static async Task<string> BuildAsync(PdfiumThread thread, PdfInputFile input, CompressOptions options, string work, int attempt, CancellationToken cancellationToken)
    {
        string pictures = Path.Combine(work, $"pictures-{attempt}.pdf");
        string final = Path.Combine(work, $"final-{attempt}.pdf");
        await File.WriteAllBytesAsync(pictures, [], cancellationToken).ConfigureAwait(false);

        RawPdf? doc = null;
        try
        {
            int pages = await thread.InvokeAsync(() =>
            {
                doc = RawPdf.Load(input.Path, input.Password);
                return doc.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            // A picture used on many pages is stored once. Replacing it page by page would store it many times.
            var uses = new Dictionary<string, int>();
            for (int page = 0; page < pages; page++)
            {
                int index = page;
                await thread.InvokeAsync(() => CountPictures(doc!, index, uses, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            }

            for (int page = 0; page < pages; page++)
            {
                int index = page;
                var candidates = await thread.InvokeAsync(() => ReadCandidates(doc!, index, options, uses, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                try
                {
                    var encoded = await Task.Run(() => candidates.Items.Select(c => (c.Object, c.RawLength, Jpeg: Encode(c, options))).ToArray(), cancellationToken).ConfigureAwait(false);
                    await thread.InvokeAsync(() => Replace(candidates.Page, encoded, input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await thread.InvokeAsync(() => PdfiumNative.FPDF_ClosePage(candidates.Page), PdfWorkPriority.Background, CancellationToken.None).ConfigureAwait(false);
                }
            }

            await thread.InvokeAsync(() => PdfComposer.Save(doc!, pictures, pictures), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
        }

        await QpdfTool.RunAsync(QpdfTool.Tight(QpdfTool.Job(pictures, final, input.Password)), input.Path, cancellationToken).ConfigureAwait(false);
        await thread.InvokeAsync(() => PdfComposer.Verify(final, input.Password, PageCountOf(input), input.Path), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        return final;
    }

    private static int PageCountOf(PdfInputFile input)
    {
        using var doc = RawPdf.Load(input.Path, input.Password);
        return doc.PageCount;
    }

    private static nint LoadPage(RawPdf doc, int index, string path)
    {
        nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
        return page != 0 ? page : throw new PdfEditException(PdfEditError.BuildFailed, path);
    }

    private static unsafe byte[] RawData(nint image)
    {
        uint length = PdfiumNative.FPDFImageObj_GetImageDataRaw(image, null, 0);
        byte[] data = new byte[length];
        fixed (byte* d = data)
        {
            _ = PdfiumNative.FPDFImageObj_GetImageDataRaw(image, d, length);
        }

        return data;
    }

    private static int CountPictures(RawPdf doc, int index, Dictionary<string, int> uses, string path)
    {
        nint page = LoadPage(doc, index, path);
        try
        {
            int count = PdfiumNative.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
                if (PdfiumNative.FPDFPageObj_GetType(obj) == PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(RawData(obj)));
                    uses[hash] = uses.GetValueOrDefault(hash) + 1;
                }
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }

        return 0;
    }

    private static unsafe (nint Page, List<Candidate> Items) ReadCandidates(RawPdf doc, int index, CompressOptions options, Dictionary<string, int> uses, string path)
    {
        nint page = LoadPage(doc, index, path);
        var items = new List<Candidate>();
        int count = PdfiumNative.FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
            if (obj == 0 || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
            {
                continue;
            }

            PdfiumNative.FPDF_IMAGEOBJ_METADATA info;
            if (PdfiumNative.FPDFImageObj_GetImageMetadata(obj, page, &info) == 0)
            {
                continue;
            }

            // Leave alone: tiny pictures, 1-bit scans (already tiny, and JPEG ruins them), and anything
            // see-through, because a JPEG can't carry its transparency.
            if (info.width < SmallestPictureSide || info.height < SmallestPictureSide || info.bits_per_pixel <= OneBitPicture
                || IsSeeThrough(doc.Handle, page, obj))
            {
                continue;
            }

            byte[] raw = RawData(obj);
            if (uses.GetValueOrDefault(Convert.ToHexString(SHA256.HashData(raw))) > 1)
            {
                continue;
            }

            nint bitmap = PdfiumNative.FPDFImageObj_GetBitmap(obj);
            if (bitmap == 0)
            {
                continue;
            }

            try
            {
                if (PdfDocument.ToBgra(bitmap) is { } picture)
                {
                    double dpi = Math.Max(info.horizontal_dpi, info.vertical_dpi);
                    double scale = dpi > options.MaxDpi ? options.MaxDpi / dpi : 1;
                    items.Add(new Candidate(obj, raw.Length, picture, scale));
                }
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }

        return (page, items);
    }

    /// <summary>PDFium has no direct "has a mask" question, so this looks at the picture as it would draw.</summary>
    private static unsafe bool IsSeeThrough(nint document, nint page, nint image)
    {
        nint bitmap = PdfiumNative.FPDFImageObj_GetRenderedBitmap(document, page, image);
        if (bitmap == 0)
        {
            return true; // can't tell, so be safe and leave it alone
        }

        try
        {
            if (PdfiumNative.FPDFBitmap_GetFormat(bitmap) != PdfiumNative.FPDFBitmap_BGRA)
            {
                return false;
            }

            int width = PdfiumNative.FPDFBitmap_GetWidth(bitmap), height = PdfiumNative.FPDFBitmap_GetHeight(bitmap);
            int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            byte* pixels = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
            for (int y = 0; y < height; y++)
            {
                byte* row = pixels + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    if (row[(x * 4) + 3] < 250)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static byte[] Encode(Candidate candidate, CompressOptions options)
    {
        var picture = candidate.Scale < 1 ? Shrunk(candidate.Picture, candidate.Scale) : candidate.Picture;
        return JpegEncoder.Encode(picture, options.JpegQuality, options.Greyscale);
    }

    private static unsafe int Replace(nint page, (nint Object, long RawLength, byte[] Jpeg)[] encoded, string path)
    {
        bool changed = false;
        foreach (var (obj, rawLength, jpeg) in encoded)
        {
            if (jpeg.Length >= rawLength * WorthwhileSaving)
            {
                continue;
            }

            fixed (byte* bytes = jpeg)
            {
                var access = new PdfiumNative.FPDF_FILEACCESS { m_FileLen = (uint)jpeg.Length, m_GetBlock = &ReadBlock, m_Param = (nint)bytes };
                nint noPages = 0;
                changed |= PdfiumNative.FPDFImageObj_LoadJpegFileInline(&noPages, 0, obj, &access) != 0;
            }
        }

        if (changed && PdfiumNative.FPDFPage_GenerateContent(page) == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        return 0;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int ReadBlock(nint param, uint position, byte* buffer, uint size)
    {
        Buffer.MemoryCopy((byte*)param + position, buffer, size, size);
        return 1;
    }

    /// <summary>Scales down by averaging the pixels each new pixel covers, which keeps detail from turning jagged.</summary>
    private static PdfBitmap Shrunk(PdfBitmap source, double scale)
    {
        int width = Math.Max(1, (int)(source.Width * scale)), height = Math.Max(1, (int)(source.Height * scale));
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int top = y * source.Height / height, bottom = Math.Max(top + 1, (y + 1) * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int left = x * source.Width / width, right = Math.Max(left + 1, (x + 1) * source.Width / width);
                int b = 0, g = 0, r = 0, count = 0;
                for (int sy = top; sy < bottom; sy++)
                {
                    int at = (sy * source.Stride) + (left * 4);
                    for (int sx = left; sx < right; sx++, at += 4)
                    {
                        b += source.Pixels[at];
                        g += source.Pixels[at + 1];
                        r += source.Pixels[at + 2];
                        count++;
                    }
                }

                int to = ((y * width) + x) * 4;
                pixels[to] = (byte)(b / count);
                pixels[to + 1] = (byte)(g / count);
                pixels[to + 2] = (byte)(r / count);
                pixels[to + 3] = 255;
            }
        }

        return new PdfBitmap(width, height, width * 4, pixels);
    }

    private static string NewWorkFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "compress", Guid.NewGuid().ToString("N"))).FullName;

    internal static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: it's in the temp folder, which Windows clears in time.
        }
    }

    private sealed record Candidate(nint Object, long RawLength, PdfBitmap Picture, double Scale);
}
