using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>A run of selected text on one page and the boxes to highlight it.</summary>
public sealed record PdfTextSelection(int PageIndex, int Start, int Length, string Text, IReadOnlyList<NormalizedRect> Areas);

public sealed partial class PdfDocument
{
    // One parsed text page is kept, so dragging a selection doesn't re-read the page on every mouse move.
    private int _textPageIndex = -1;
    private nint _textPage;

    /// <summary>
    /// Makes recognised words selectable on this open document straight away. The file only changes
    /// if this document is saved later, and then the words are saved with it.
    /// </summary>
    public Task<bool> AddLiveTextAsync(Editing.OcrPage ocr, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(ocr.PageIndex);
        return Run(() =>
        {
            CloseTextPage(); // it describes the page as it was
            using var fonts = new Editing.FontBook();
            bool added = Editing.OcrTextWriter.AddWords(_handle, GetPage(ocr.PageIndex), ocr.Words, fonts);

            // Text engine positions are only right for words read from page content, so read the page afresh.
            ForgetPage(ocr.PageIndex);
            return added;
        }, cancellationToken);
    }

    /// <summary>Returns all text on a page, in PDFium's reading order.</summary>
    public Task<string> GetPageTextAsync(
        int pageIndex,
        PdfWorkPriority priority = PdfWorkPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() =>
        {
            nint textPage = GetTextPage(pageIndex);
            int count = textPage == 0 ? 0 : PdfiumNative.FPDFText_CountChars(textPage);
            return count <= 0 ? string.Empty : GetText(textPage, 0, count);
        }, cancellationToken, priority);
    }

    /// <summary>
    /// Finds every match of <paramref name="query"/> on one page. The UI calls this page by page,
    /// so a search through 1,000 pages shows results as they arrive and can be cancelled.
    /// </summary>
    public unsafe Task<IReadOnlyList<PdfSearchHit>> FindOnPageAsync(
        int pageIndex,
        string query,
        PdfSearchOptions options = PdfSearchOptions.None,
        CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<PdfSearchHit>>([]);
        }

        uint flags = (options.HasFlag(PdfSearchOptions.MatchCase) ? PdfiumNative.FPDF_MATCHCASE : 0)
                   | (options.HasFlag(PdfSearchOptions.WholeWord) ? PdfiumNative.FPDF_MATCHWHOLEWORD : 0);

        return Run<IReadOnlyList<PdfSearchHit>>(() =>
        {
            var hits = new List<PdfSearchHit>();
            nint textPage = GetTextPage(pageIndex);
            if (textPage == 0)
            {
                return hits;
            }

            nint page = GetPage(pageIndex);
            int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
            nint search;
            fixed (char* q = query + '\0')
            {
                search = PdfiumNative.FPDFText_FindStart(textPage, q, flags, 0);
            }

            if (search == 0)
            {
                return hits;
            }

            try
            {
                while (PdfiumNative.FPDFText_FindNext(search) != 0 && hits.Count < 10_000)
                {
                    int start = PdfiumNative.FPDFText_GetSchResultIndex(search);
                    int length = PdfiumNative.FPDFText_GetSchCount(search);
                    int ctxStart = Math.Max(0, start - 40);
                    int ctxEnd = Math.Min(totalChars, start + length + 60);
                    string context = CollapseWhitespace(GetText(textPage, ctxStart, ctxEnd - ctxStart));
                    hits.Add(new PdfSearchHit(pageIndex, start, length, GetAreas(page, textPage, start, length), context));
                }
            }
            finally
            {
                PdfiumNative.FPDFText_FindClose(search);
            }

            return hits;
        }, cancellationToken, PdfWorkPriority.Background);
    }

    /// <summary>The character under a point (fractions of the displayed page), or -1 if there is none nearby.</summary>
    public unsafe Task<int> GetCharIndexAtAsync(int pageIndex, double x, double y, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() =>
        {
            nint textPage = GetTextPage(pageIndex);
            if (textPage == 0)
            {
                return -1;
            }

            const int Scale = 100_000;
            double px, py;
            if (PdfiumNative.FPDF_DeviceToPage(GetPage(pageIndex), 0, 0, Scale, Scale, 0, (int)(x * Scale), (int)(y * Scale), &px, &py) == 0)
            {
                return -1;
            }

            return PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, px, py, 6, 6);
        }, cancellationToken);
    }

    /// <summary>The text and highlight boxes between two character indexes (in either order, inclusive).</summary>
    public Task<PdfTextSelection> GetSelectionAsync(int pageIndex, int fromChar, int toChar, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() =>
        {
            nint textPage = GetTextPage(pageIndex);
            int total = textPage == 0 ? 0 : PdfiumNative.FPDFText_CountChars(textPage);
            int start = Math.Clamp(Math.Min(fromChar, toChar), 0, Math.Max(0, total - 1));
            int end = Math.Clamp(Math.Max(fromChar, toChar), 0, Math.Max(0, total - 1));
            int length = total == 0 ? 0 : end - start + 1;
            return new PdfTextSelection(
                pageIndex, start, length,
                GetText(textPage, start, length),
                length == 0 ? [] : GetAreas(GetPage(pageIndex), textPage, start, length));
        }, cancellationToken);
    }

    /// <summary>Selects the word around a character (for double-click).</summary>
    public async Task<PdfTextSelection> GetWordAtAsync(int pageIndex, int charIndex, CancellationToken cancellationToken = default)
    {
        string text = await GetPageTextAsync(pageIndex, PdfWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        if (charIndex < 0 || charIndex >= text.Length)
        {
            return new PdfTextSelection(pageIndex, 0, 0, string.Empty, []);
        }

        int start = charIndex, end = charIndex;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length - 1 && IsWordChar(text[end + 1]))
        {
            end++;
        }

        return await GetSelectionAsync(pageIndex, start, end, cancellationToken).ConfigureAwait(false);

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-' or '_';
    }

    private nint GetTextPage(int pageIndex)
    {
        if (_textPageIndex == pageIndex && _textPage != 0)
        {
            return _textPage;
        }

        CloseTextPage();
        _textPage = PdfiumNative.FPDFText_LoadPage(GetPage(pageIndex));
        _textPageIndex = _textPage == 0 ? -1 : pageIndex;
        return _textPage;
    }

    /// <summary>Must run before the page it belongs to is closed.</summary>
    private void CloseTextPage()
    {
        if (_textPage != 0)
        {
            PdfiumNative.FPDFText_ClosePage(_textPage);
        }

        _textPage = 0;
        _textPageIndex = -1;
    }

    private static unsafe List<NormalizedRect> GetAreas(nint page, nint textPage, int start, int length)
    {
        var areas = new List<NormalizedRect>();
        int rects = PdfiumNative.FPDFText_CountRects(textPage, start, length);
        for (int r = 0; r < rects; r++)
        {
            double left, top, right, bottom;
            if (PdfiumNative.FPDFText_GetRect(textPage, r, &left, &top, &right, &bottom) != 0)
            {
                areas.Add(Normalize(page, left, top, right, bottom));
            }
        }

        return areas;
    }

    private static T WithTextPage<T>(nint page, Func<nint, T> work)
    {
        nint textPage = PdfiumNative.FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            return work(0);
        }

        try
        {
            return work(textPage);
        }
        finally
        {
            PdfiumNative.FPDFText_ClosePage(textPage);
        }
    }

    internal static string AllText(nint textPage)
    {
        int count = PdfiumNative.FPDFText_CountChars(textPage);
        return count <= 0 ? string.Empty : GetText(textPage, 0, count);
    }

    private static unsafe string GetText(nint textPage, int start, int count)
    {
        if (textPage == 0 || count <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[count + 1];
        fixed (char* b = buffer)
        {
            int written = PdfiumNative.FPDFText_GetText(textPage, start, count, (ushort*)b);
            return new string(buffer, 0, Math.Max(0, written - 1)); // drop the terminator
        }
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
