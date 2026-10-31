using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// Changes a line of text in the page's own content, so the old words are really gone. This only works when the
/// line sits in tidy pieces and the font already has every letter wanted, because PDF fonts usually hold just the
/// letters they use. Everything is checked by reading the finished file again; when in doubt it says no, and the caller covers and retypes instead.
/// </summary>
internal static class PdfTextReplacer
{
    public static async Task<bool> TryAsync(PdfiumThread thread, PdfInputFile input, int pageIndex, NormalizedRect line, string oldText, string newText, string outputPath, CancellationToken cancellationToken)
    {
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "text", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string built = Path.Combine(work, "built.pdf");
            await File.WriteAllBytesAsync(built, [], cancellationToken).ConfigureAwait(false);
            var pages = await thread.InvokeAsync(() => Build(input, pageIndex, line, oldText, newText, built), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            if (pages is not int count
                || !await thread.InvokeAsync(() => Check(input, built, pageIndex, line, oldText, newText), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await PdfCompressor.CommitAsync(thread, built, input.Password, count, outputPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    private static unsafe int? Build(PdfInputFile input, int pageIndex, NormalizedRect line, string oldText, string newText, string built)
    {
        using var doc = RawPdf.Load(input.Path, input.Password);
        nint page = PdfCompressor.LoadPage(doc, pageIndex, input.Path);
        nint textPage = 0;
        try
        {
            textPage = PdfiumNative.FPDFText_LoadPage(page);
            if (textPage == 0)
            {
                return null;
            }

            var a = PdfDocument.ToUser(page, new PagePoint(line.X, line.Y));
            var b = PdfDocument.ToUser(page, new PagePoint(line.Right, line.Bottom));
            double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y, b.Y);

            var pieces = new List<(nint Object, double Left, string Text)>();
            var everyPiece = new List<(nint Object, string Text)>();
            int count = PdfiumNative.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                nint obj = PdfiumNative.FPDFPage_GetObject(page, i);
                if (obj == 0 || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                {
                    continue;
                }

                string text = TextOf(obj, textPage);
                everyPiece.Add((obj, text));
                float l, d, r, t;
                if (PdfiumNative.FPDFPageObj_GetBounds(obj, &l, &d, &r, &t) == 0)
                {
                    continue;
                }

                double middle = (d + t) / 2.0;
                if (middle >= bottom && middle <= top && r > left && l < right)
                {
                    pieces.Add((obj, l, text));
                }
            }

            pieces.Sort((x, y) => x.Left.CompareTo(y.Left));
            if (pieces.Count == 0 || Plain(string.Concat(pieces.Select(p => p.Text))) != Plain(oldText))
            {
                return null; // the line is split in some way that can't be put back reliably
            }

            // Every letter must already be drawn somewhere on this page in this font.
            nint font = PdfiumNative.FPDFTextObj_GetFont(pieces[0].Object);
            var letters = new HashSet<char>(everyPiece.Where(p => PdfiumNative.FPDFTextObj_GetFont(p.Object) == font).SelectMany(p => p.Text));
            if (font == 0 || !newText.All(letters.Contains))
            {
                return null;
            }

            fixed (char* words = newText + '\0')
            {
                if (PdfiumNative.FPDFText_SetText(pieces[0].Object, words) == 0)
                {
                    return null;
                }
            }

            foreach (var (obj, _, _) in pieces.Skip(1))
            {
                if (PdfiumNative.FPDFPage_RemoveObject(page, obj) != 0)
                {
                    PdfiumNative.FPDFPageObj_Destroy(obj);
                }
            }

            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                return null;
            }

            int pages = doc.PageCount;
            PdfComposer.Save(doc, built, input.Path);
            return pages;
        }
        finally
        {
            if (textPage != 0)
            {
                PdfiumNative.FPDFText_ClosePage(textPage);
            }

            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>Reads the result afresh: the new words must read correctly, the old ones must have gone, and something must actually be drawn.</summary>
    private static bool Check(PdfInputFile input, string built, int pageIndex, NormalizedRect line, string oldText, string newText)
    {
        string before = PageText(input.Path, input.Password, pageIndex);
        string after = PageText(built, input.Password, pageIndex);
        string wanted = Tidy(newText);
        if (!after.Contains(wanted, StringComparison.Ordinal))
        {
            return false;
        }

        string old = Tidy(oldText);
        if (Occurrences(after, old) >= Occurrences(before, old) && !wanted.Contains(old, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(newText) || DrawsSomething(built, input.Password, pageIndex, line);
    }

    private static string PageText(string path, string? password, int pageIndex)
    {
        using var doc = RawPdf.Load(path, password);
        nint page = PdfCompressor.LoadPage(doc, pageIndex, path);
        try
        {
            nint textPage = PdfiumNative.FPDFText_LoadPage(page);
            if (textPage == 0)
            {
                return string.Empty;
            }

            try
            {
                return Tidy(PdfDocument.AllText(textPage));
            }
            finally
            {
                PdfiumNative.FPDFText_ClosePage(textPage);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static unsafe bool DrawsSomething(string path, string? password, int pageIndex, NormalizedRect line)
    {
        const int Width = 1000;
        using var doc = RawPdf.Load(path, password);
        nint page = PdfCompressor.LoadPage(doc, pageIndex, path);
        try
        {
            int height = Math.Max(1, (int)(Width * PdfiumNative.FPDF_GetPageHeightF(page) / Math.Max(1, PdfiumNative.FPDF_GetPageWidthF(page))));
            byte[] pixels = new byte[Width * height * 4];
            fixed (byte* p = pixels)
            {
                nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(Width, height, PdfiumNative.FPDFBitmap_BGRA, (nint)p, Width * 4);
                if (bitmap == 0)
                {
                    return false;
                }

                _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, Width, height, 0xFFFFFFFF);
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, Width, height, 0, 0);
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            int top = Math.Clamp((int)(line.Y * height), 0, height - 1), bottom = Math.Clamp((int)(line.Bottom * height), top + 1, height);
            int left = Math.Clamp((int)(line.X * Width), 0, Width - 1);
            int first = (top * Width * 4) + (left * 4);
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < Width; x++)
                {
                    int at = ((y * Width) + x) * 4;
                    if (Math.Abs(pixels[at] - pixels[first]) + Math.Abs(pixels[at + 1] - pixels[first + 1]) + Math.Abs(pixels[at + 2] - pixels[first + 2]) > 60)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static unsafe string TextOf(nint obj, nint textPage)
    {
        uint bytes = PdfiumNative.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        char[] buffer = new char[bytes / 2];
        fixed (char* b = buffer)
        {
            _ = PdfiumNative.FPDFTextObj_GetText(obj, textPage, b, bytes);
        }

        return new string(buffer).TrimEnd('\0');
    }

    private static int Occurrences(string text, string part)
    {
        if (part.Length == 0)
        {
            return 0;
        }

        int found = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }

    private static string Plain(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '\0'));

    private static string Tidy(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
