using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>How a text file is laid out: the paper, the type size and the margin all round, in points.</summary>
public sealed record TextPageOptions(PdfPageSize Paper, float FontSize = 10, double Margin = 54)
{
    // Courier's letters are all 600/1000 of the type size wide, and lines sit 1.2 sizes apart, like a typewriter's.
    private const double LetterWidth = 0.6, LineGap = 1.2;

    /// <summary>How many letters fit across a line.</summary>
    public int Columns => Math.Max(1, (int)((Paper.Width - (2 * Margin)) / (FontSize * LetterWidth)));

    /// <summary>How many lines fit down a page.</summary>
    public int LinesPerPage => Math.Max(1, (int)((Paper.Height - (2 * Margin)) / (FontSize * LineGap)));

    internal double LineHeight => FontSize * LineGap;
}

/// <summary>
/// Makes a PDF from lines of plain text, in a typewriter font so columns and tables made of spaces stay lined up.
/// The lines come already wrapped to <see cref="TextPageOptions.Columns"/>; a line holding just a form feed (\f)
/// starts a new page. The text is real text, so it can be searched and copied.
/// </summary>
internal static class TextToPdfWriter
{
    public static async Task<int> WriteAsync(PdfiumThread thread, IReadOnlyList<string> lines, TextPageOptions options, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pages = Paginate(lines, options.LinesPerPage);
        outputPath = Path.GetFullPath(outputPath);
        RawPdf? doc = null;
        nint font = 0;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            bool western = lines.All(l => l.All(c => c < 256));
            await thread.InvokeAsync(() =>
            {
                doc = RawPdf.CreateNew();
                font = LoadFont(doc, western);
                return font != 0 ? 0 : throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = i;
                await thread.InvokeAsync(() => AddPage(doc!, font, index, pages[index], options, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                progress?.Report((i + 1.0) / pages.Count);
            }

            await thread.InvokeAsync(() => PdfComposer.Save(doc!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, null, pages.Count, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
            return pages.Count;
        }
        finally
        {
            await thread.InvokeAsync(() =>
            {
                if (font != 0)
                {
                    PdfiumNative.FPDFFont_Close(font);
                }

                doc?.Dispose();
                return 0;
            }, PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>Lines shared out into pages. There's always at least one page, even for an empty file.</summary>
    internal static List<List<string>> Paginate(IReadOnlyList<string> lines, int linesPerPage)
    {
        var pages = new List<List<string>> { new() };
        foreach (string line in lines)
        {
            if (line == "\f")
            {
                pages.Add([]);
            }
            else
            {
                if (pages[^1].Count >= linesPerPage)
                {
                    pages.Add([]);
                }

                pages[^1].Add(line);
            }
        }

        // A form feed at the very end (common in printouts) shouldn't leave an empty last page.
        if (pages.Count > 1 && pages[^1].Count == 0)
        {
            pages.RemoveAt(pages.Count - 1);
        }

        return pages;
    }

    /// <summary>Courier needs nothing embedded but only has Western letters; for others, Windows' Courier New is embedded.</summary>
    private static unsafe nint LoadFont(RawPdf doc, bool western)
    {
        if (western)
        {
            byte[] name = Encoding.ASCII.GetBytes("Courier\0");
            fixed (byte* n = name)
            {
                return PdfiumNative.FPDFText_LoadStandardFont(doc.Handle, n);
            }
        }

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "cour.ttf");
        if (!File.Exists(path))
        {
            return LoadFont(doc, western: true);
        }

        byte[] data = File.ReadAllBytes(path);
        fixed (byte* d = data)
        {
            return PdfiumNative.FPDFText_LoadFont(doc.Handle, d, (uint)data.Length, PdfiumNative.FPDF_FONT_TRUETYPE, 1);
        }
    }

    private static unsafe int AddPage(RawPdf doc, nint font, int index, List<string> lines, TextPageOptions options, string outputPath)
    {
        nint page = PdfiumNative.FPDFPage_New(doc.Handle, index, options.Paper.Width, options.Paper.Height);
        if (page == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
        }

        try
        {
            // The first line's baseline sits one type size below the top margin.
            double y = options.Paper.Height - options.Margin - options.FontSize;
            foreach (string line in lines)
            {
                if (line.Length > 0 && !string.IsNullOrWhiteSpace(line))
                {
                    nint text = PdfiumNative.FPDFPageObj_CreateTextObj(doc.Handle, font, options.FontSize);
                    if (text == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
                    }

                    fixed (char* t = line.TrimEnd(' ') + '\0')
                    {
                        _ = PdfiumNative.FPDFText_SetText(text, t);
                    }

                    PdfiumNative.FPDFPageObj_Transform(text, 1, 0, 0, 1, options.Margin, y);
                    PdfiumNative.FPDFPage_InsertObject(page, text);
                }

                y -= options.LineHeight;
            }

            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, outputPath);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }

        return index;
    }
}
