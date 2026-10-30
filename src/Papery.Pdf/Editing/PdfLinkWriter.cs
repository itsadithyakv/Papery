using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>A link to take off a page, found by where it is.</summary>
public sealed record PdfLinkRemoval(int PageIndex, NormalizedRect Area);

/// <summary>A new link: to a web address or to a page of this PDF.</summary>
public sealed record NewPdfLink(int PageIndex, NormalizedRect Area, string? Uri, int? TargetPage);

/// <summary>
/// Changes to links and bookmarks. PDFium reads them but can't write them, so these use PDFsharp,
/// which is why the file must be saved first. All of it goes through the checked save.
/// </summary>
internal static class PdfLinkWriter
{
    // How far apart two link rectangles can be and still be the same link, in points.
    private const double SameRectTolerance = 1.5;

    public static async Task ApplyAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<PdfLinkRemoval> remove, IReadOnlyList<NewPdfLink> add, string outputPath, CancellationToken cancellationToken)
    {
        // Where things are in PDF space, worked out by PDFium so rotation and crop are right.
        var (removeRects, addRects, pages) = await thread.InvokeAsync(() =>
        {
            using var doc = RawPdf.Load(input.Path, input.Password);
            var removing = remove.Select(r => (r.PageIndex, Rect: ToRect(doc, r.PageIndex, r.Area, input.Path))).ToArray();
            var adding = add.Select(a => (Link: a, Rect: ToRect(doc, a.PageIndex, a.Area, input.Path))).ToArray();
            return (removing, adding, doc.PageCount);
        }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

        await WriteCheckedAsync(thread, input, pages, outputPath, temp => Task.Run(() =>
        {
            using var doc = Open(input);
            foreach (var (pageIndex, rect) in removeRects)
            {
                RemoveLink(doc.Pages[pageIndex], rect);
            }

            foreach (var (link, rect) in addRects)
            {
                var page = doc.Pages[link.PageIndex];
                if (link.TargetPage is int target)
                {
                    page.AddDocumentLink(rect, target + 1); // PDFsharp counts pages from 1
                }
                else
                {
                    page.AddWebLink(rect, link.Uri ?? string.Empty);
                }
            }

            SaveTo(doc, temp);
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetOutlineAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<PdfOutlineItem> outline, string outputPath, CancellationToken cancellationToken)
    {
        int pages = await thread.InvokeAsync(() =>
        {
            using var doc = RawPdf.Load(input.Path, input.Password);
            return doc.PageCount;
        }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

        await WriteCheckedAsync(thread, input, pages, outputPath, temp => Task.Run(() => PdfOutlineWriter.Write(input.Path, temp, outline, null, input.Password), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static async Task RestoreAsync(PdfiumThread thread, string copyPath, string? password, string outputPath, CancellationToken cancellationToken)
    {
        int pages = await thread.InvokeAsync(() =>
        {
            using var doc = RawPdf.Load(copyPath, password);
            return doc.PageCount;
        }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

        await WriteCheckedAsync(thread, new PdfInputFile(copyPath, password), pages, outputPath, async temp =>
        {
            await using var from = new FileStream(copyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var to = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            to.SetLength(0);
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
            await to.FlushAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCheckedAsync(PdfiumThread thread, PdfInputFile input, int pages, string outputPath, Func<string, Task> write, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await write(temp).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, input.Password, pages, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or PdfReaderException or System.Reflection.TargetInvocationException)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, input.Path, ex);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    private static PdfRectangle ToRect(RawPdf doc, int pageIndex, NormalizedRect area, string path)
    {
        nint page = Native.PdfiumNative.FPDF_LoadPage(doc.Handle, pageIndex);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        try
        {
            var a = PdfDocument.ToUser(page, new PagePoint(area.X, area.Y));
            var b = PdfDocument.ToUser(page, new PagePoint(area.Right, area.Bottom));
            return new PdfRectangle(new PdfSharp.Drawing.XRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
        }
        finally
        {
            Native.PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static void RemoveLink(PdfPage page, PdfRectangle area)
    {
        for (int i = page.Annotations.Count - 1; i >= 0; i--)
        {
            var annotation = page.Annotations[i];
            if (annotation.Elements.GetName("/Subtype") == "/Link" && IsSame(annotation.Elements.GetRectangle("/Rect"), area))
            {
                page.Annotations.Remove(annotation);
                return;
            }
        }
    }

    private static bool IsSame(PdfRectangle a, PdfRectangle b) =>
        Math.Abs(a.X1 - b.X1) <= SameRectTolerance && Math.Abs(a.X2 - b.X2) <= SameRectTolerance &&
        Math.Abs(a.Y1 - b.Y1) <= SameRectTolerance && Math.Abs(a.Y2 - b.Y2) <= SameRectTolerance;

    private static PdfSharp.Pdf.PdfDocument Open(PdfInputFile input) => input.Password is null
        ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Modify)
        : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Modify);

    private static void SaveTo(PdfSharp.Pdf.PdfDocument doc, string temp)
    {
        // Open (not Create): the temp file is hidden, and Create refuses to overwrite hidden files.
        using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
        stream.SetLength(0);
        doc.Save(stream, closeStream: false);
        stream.Flush(flushToDisk: true);
    }
}
