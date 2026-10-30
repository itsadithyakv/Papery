using System.Text;
using Papery.Pdf.Editing;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>A word on a page, with where it is and how it looks.</summary>
public sealed record PdfWord(string Text, NormalizedRect Area, int Start, int Length, float FontSize, bool Bold, bool Italic, string FontName, PdfColor Color);

public sealed partial class PdfDocument
{
    /// <summary>The words on a page, in reading order.</summary>
    public Task<IReadOnlyList<PdfWord>> GetPageWordsAsync(int pageIndex, PdfWorkPriority priority = PdfWorkPriority.Background, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<PdfWord>>(() =>
        {
            nint textPage = GetTextPage(pageIndex);
            return textPage == 0 ? [] : ReadWords(GetPage(pageIndex), textPage);
        }, cancellationToken, priority);
    }

    /// <summary>The characters of the whole line under a character (for changing existing text).</summary>
    public async Task<IReadOnlyList<PdfWord>> GetLineAtAsync(int pageIndex, int charIndex, CancellationToken cancellationToken = default)
    {
        var words = await GetPageWordsAsync(pageIndex, PdfWorkPriority.Visible, cancellationToken).ConfigureAwait(false);
        var hit = words.FirstOrDefault(w => charIndex >= w.Start && charIndex < w.Start + w.Length)
            ?? words.OrderBy(w => Math.Abs(w.Start - charIndex)).FirstOrDefault();
        if (hit == null)
        {
            return [];
        }

        double middle = hit.Area.Y + (hit.Area.Height / 2);
        return words.Where(w => w.Area.Y <= middle && w.Area.Bottom >= middle).OrderBy(w => w.Area.X).ToArray();
    }

    internal static unsafe List<PdfWord> ReadWords(nint page, nint textPage)
    {
        var words = new List<PdfWord>();
        int count = PdfiumNative.FPDFText_CountChars(textPage);
        var text = new StringBuilder();
        int start = -1;
        double left = 0, right = 0, top = 0, bottom = 0;

        void Finish()
        {
            if (text.Length == 0)
            {
                return;
            }

            float size = (float)PdfiumNative.FPDFText_GetFontSize(textPage, start);
            int weight = PdfiumNative.FPDFText_GetFontWeight(textPage, start);
            byte[] nameBytes = new byte[128];
            int flags = 0;
            uint nameLength;
            fixed (byte* n = nameBytes)
            {
                nameLength = PdfiumNative.FPDFText_GetFontInfo(textPage, start, n, (uint)nameBytes.Length, &flags);
            }

            string name = nameLength is > 0 and <= 128 ? Encoding.UTF8.GetString(nameBytes, 0, (int)nameLength - 1) : string.Empty;
            uint r = 0, g = 0, b = 0, a = 255;
            _ = PdfiumNative.FPDFText_GetFillColor(textPage, start, &r, &g, &b, &a);
            bool bold = weight >= 600 || name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            bool italic = (flags & 0x40) != 0 || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            words.Add(new PdfWord(text.ToString(), Normalize(page, left, top, right, bottom), start, text.Length, size, bold, italic, name, new PdfColor((byte)r, (byte)g, (byte)b)));
            text.Clear();
            start = -1;
        }

        for (int i = 0; i < count; i++)
        {
            uint unicode = PdfiumNative.FPDFText_GetUnicode(textPage, i);
            if (unicode == 0 || unicode > 0xFFFF ? unicode == 0 : char.IsWhiteSpace((char)unicode) || unicode is 0xFFFE or 0xFFFF)
            {
                Finish();
                continue;
            }

            double l, r, b, t;
            if (PdfiumNative.FPDFText_GetCharBox(textPage, i, &l, &r, &b, &t) == 0)
            {
                Finish();
                continue;
            }

            if (start < 0)
            {
                start = i;
                (left, right, top, bottom) = (l, r, t, b);
            }
            else
            {
                left = Math.Min(left, l);
                right = Math.Max(right, r);
                top = Math.Max(top, t);
                bottom = Math.Min(bottom, b);
            }

            text.Append(char.ConvertFromUtf32((int)(unicode is >= 0xD800 and <= 0xDFFF ? 0xFFFD : unicode)));
        }

        Finish();
        return words;
    }
}
