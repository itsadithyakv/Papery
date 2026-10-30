using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>A word that text recognition read off a page, and where it is.</summary>
public sealed record OcrWord(string Text, NormalizedRect Area);

/// <summary>What recognition found on one page.</summary>
public sealed record OcrPage(int PageIndex, IReadOnlyList<OcrWord> Words);

/// <summary>Fonts for many pieces of text in one document, so each is only added to the file once.</summary>
internal sealed class FontBook : IDisposable
{
    private readonly Dictionary<(TextLook Look, bool Western), nint> _fonts = [];

    public nint Get(TextLook look, bool western, Func<nint> load)
    {
        if (!_fonts.TryGetValue((look, western), out nint font))
        {
            font = load();
            _fonts[(look, western)] = font;
        }

        return font;
    }

    public void Dispose()
    {
        foreach (nint font in _fonts.Values.Where(f => f != 0))
        {
            PdfiumNative.FPDFFont_Close(font);
        }

        _fonts.Clear();
    }
}

/// <summary>
/// Adds an invisible layer of words over a scan, so it can be searched, selected and copied while it
/// looks exactly the same. Everything goes through the checked save.
/// </summary>
internal static class OcrTextWriter
{
    public static async Task ApplyAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<OcrPage> pages, string outputPath, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        RawPdf? doc = null;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            int count = await thread.InvokeAsync(() =>
            {
                doc = RawPdf.Load(input.Path, input.Password);
                using var fonts = new FontBook();
                foreach (var ocr in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, ocr.PageIndex);
                    if (page == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                    }

                    try
                    {
                        if (!AddWords(doc.Handle, page, ocr.Words, fonts))
                        {
                            throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                        }
                    }
                    finally
                    {
                        PdfiumNative.FPDF_ClosePage(page);
                    }
                }

                return doc.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            await thread.InvokeAsync(() => PdfComposer.Save(doc!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, input.Password, count, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Puts words on a loaded page and rebuilds its content. PDFium thread only.</summary>
    internal static bool AddWords(nint document, nint page, IReadOnlyList<OcrWord> words, FontBook fonts)
    {
        var builder = new AnnotationObjects(document, page, 0, new PdfColor(0, 0, 0), fonts);
        foreach (var word in words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Area is { Width: > 0, Height: > 0 }))
        {
            builder.InvisibleWord(word.Text, word.Area);
        }

        return PdfiumNative.FPDFPage_GenerateContent(page) != 0;
    }
}
