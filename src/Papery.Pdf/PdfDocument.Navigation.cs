using System.Globalization;
using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// All clickable areas on a page: link annotations (internal jumps and web links) plus
    /// web addresses written as plain text, which Papery makes clickable too.
    /// </summary>
    public unsafe Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<PdfLink>>(() =>
        {
            var links = new List<PdfLink>();
            nint page = GetPage(pageIndex);

            int position = 0;
            nint link;
            while (links.Count < 5_000 && PdfiumNative.FPDFLink_Enumerate(page, &position, &link) != 0)
            {
                PdfiumNative.FS_RECTF rect;
                if (PdfiumNative.FPDFLink_GetAnnotRect(link, &rect) == 0)
                {
                    continue;
                }

                var area = Normalize(page, rect.Left, rect.Top, rect.Right, rect.Bottom);
                nint dest = PdfiumNative.FPDFLink_GetDest(_handle, link);
                string? uri = null;
                if (dest == 0)
                {
                    nint action = PdfiumNative.FPDFLink_GetAction(link);
                    uint type = action == 0 ? 0 : PdfiumNative.FPDFAction_GetType(action);
                    if (type == PdfiumNative.PDFACTION_GOTO)
                    {
                        dest = PdfiumNative.FPDFAction_GetDest(_handle, action);
                    }
                    else if (type == PdfiumNative.PDFACTION_URI)
                    {
                        uri = GetUri(action);
                    }
                }

                if (dest != 0)
                {
                    var (target, y) = ResolveDest(dest);
                    if (target != null)
                    {
                        links.Add(new PdfLink(area, target, y, null));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(uri))
                {
                    links.Add(new PdfLink(area, null, null, uri));
                }
            }

            // Restore the current page (ResolveDest may have loaded the target page).
            page = GetPage(pageIndex);
            WithTextPage(page, textPage =>
            {
                if (textPage == 0)
                {
                    return 0;
                }

                nint webLinks = PdfiumNative.FPDFLink_LoadWebLinks(textPage);
                if (webLinks == 0)
                {
                    return 0;
                }

                try
                {
                    int count = PdfiumNative.FPDFLink_CountWebLinks(webLinks);
                    for (int i = 0; i < count && links.Count < 5_000; i++)
                    {
                        int length = PdfiumNative.FPDFLink_GetURL(webLinks, i, null, 0);
                        if (length <= 1)
                        {
                            continue;
                        }

                        var buffer = new char[length];
                        fixed (char* b = buffer)
                        {
                            _ = PdfiumNative.FPDFLink_GetURL(webLinks, i, b, length);
                        }

                        string url = new(buffer, 0, length - 1);
                        int rects = PdfiumNative.FPDFLink_CountRects(webLinks, i);
                        for (int r = 0; r < rects; r++)
                        {
                            double left, top, right, bottom;
                            if (PdfiumNative.FPDFLink_GetRect(webLinks, i, r, &left, &top, &right, &bottom) != 0)
                            {
                                links.Add(new PdfLink(Normalize(page, left, top, right, bottom), null, null, url, IsDetected: true));
                            }
                        }
                    }
                }
                finally
                {
                    PdfiumNative.FPDFLink_CloseWebLinks(webLinks);
                }

                return 0;
            });

            return links;
        }, cancellationToken, PdfWorkPriority.Nearby);
    }

    /// <summary>The bookmarks (outline) tree. Protected against looping or absurdly large outlines.</summary>
    public unsafe Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default)
    {
        return Run<IReadOnlyList<PdfOutlineItem>>(() =>
        {
            var visited = new HashSet<nint>();
            int budget = 20_000;
            return ReadChildren(0, depth: 0);

            List<PdfOutlineItem> ReadChildren(nint parent, int depth)
            {
                var items = new List<PdfOutlineItem>();
                if (depth > 32)
                {
                    return items;
                }

                for (nint bookmark = PdfiumNative.FPDFBookmark_GetFirstChild(_handle, parent);
                     bookmark != 0 && budget-- > 0 && visited.Add(bookmark);
                     bookmark = PdfiumNative.FPDFBookmark_GetNextSibling(_handle, bookmark))
                {
                    string title = ReadUtf16(buffer => PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer.Ptr, buffer.Length));
                    nint dest = PdfiumNative.FPDFBookmark_GetDest(_handle, bookmark);
                    if (dest == 0)
                    {
                        nint action = PdfiumNative.FPDFBookmark_GetAction(bookmark);
                        if (action != 0 && PdfiumNative.FPDFAction_GetType(action) == PdfiumNative.PDFACTION_GOTO)
                        {
                            dest = PdfiumNative.FPDFAction_GetDest(_handle, action);
                        }
                    }

                    int? page = dest == 0 ? null : PdfiumNative.FPDFDest_GetDestPageIndex(_handle, dest);
                    if (page < 0 || page >= PageCount)
                    {
                        page = null;
                    }

                    items.Add(new PdfOutlineItem(string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim(), page, ReadChildren(bookmark, depth + 1)));
                }

                return items;
            }
        }, cancellationToken, PdfWorkPriority.Nearby);
    }

    public Task<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        return Run(() => new PdfMetadata(
            Meta("Title"),
            Meta("Author"),
            Meta("Subject"),
            Meta("Keywords"),
            Meta("Creator"),
            Meta("Producer"),
            ParsePdfDate(Meta("CreationDate")),
            ParsePdfDate(Meta("ModDate"))), cancellationToken);

        unsafe string? Meta(string tag)
        {
            byte[] tagBytes = Encoding.ASCII.GetBytes(tag + '\0');
            fixed (byte* t = tagBytes)
            {
                byte* tp = t;
                string value = ReadUtf16(buffer => PdfiumNative.FPDF_GetMetaText(_handle, tp, buffer.Ptr, buffer.Length));
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
    }

    /// <summary>
    /// Parses PDF dates like "D:20240131093000+01'00'". Returns null for anything unparseable
    /// (real-world files contain all sorts of garbage here).
    /// </summary>
    public static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        string digits = new(s.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length < 4)
        {
            return null;
        }

        digits = digits.PadRight(14, '0');
        if (digits.Length > 14)
        {
            digits = digits[..14];
        }

        // Months/days of 00 are allowed in PDF (meaning "unknown"): treat as 01.
        if (digits[4..6] == "00")
        {
            digits = digits[..4] + "01" + digits[6..];
        }

        if (digits[6..8] == "00")
        {
            digits = digits[..6] + "01" + digits[8..];
        }

        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        string rest = s[Math.Min(s.Length, s.TakeWhile(char.IsAsciiDigit).Count())..];
        if (rest.Length >= 3 && (rest[0] == '+' || rest[0] == '-'))
        {
            string tz = new(rest[1..].Where(char.IsAsciiDigit).ToArray());
            if (tz.Length >= 2 && int.TryParse(tz[..2], CultureInfo.InvariantCulture, out int hours))
            {
                int minutes = tz.Length >= 4 && int.TryParse(tz[2..4], CultureInfo.InvariantCulture, out int m) ? m : 0;
                if (hours <= 14 && minutes < 60)
                {
                    offset = new TimeSpan(hours, minutes, 0) * (rest[0] == '-' ? -1 : 1);
                }
            }
        }

        return new DateTimeOffset(date, offset);
    }

    private unsafe (int? Page, double? Y) ResolveDest(nint dest)
    {
        int target = PdfiumNative.FPDFDest_GetDestPageIndex(_handle, dest);
        if (target < 0 || target >= PageCount)
        {
            return (null, null);
        }

        int hasX, hasY, hasZoom;
        float x, y, zoom;
        if (PdfiumNative.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0 && hasY != 0)
        {
            var point = Normalize(GetPage(target), x, y, x, y);
            return (target, Math.Clamp(point.Y, 0, 1));
        }

        return (target, null);
    }

    private unsafe string? GetUri(nint action)
    {
        uint length = PdfiumNative.FPDFAction_GetURIPath(_handle, action, null, 0);
        if (length <= 1 || length > 64 * 1024)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* b = buffer)
        {
            _ = PdfiumNative.FPDFAction_GetURIPath(_handle, action, b, length);
        }

        return Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
    }

    private readonly unsafe struct NativeBuffer(void* ptr, uint length)
    {
        public void* Ptr { get; } = ptr;

        public uint Length { get; } = length;
    }

    /// <summary>Calls a PDFium "get UTF-16 string" function twice: once for the size, once for the text.</summary>
    private static unsafe string ReadUtf16(Func<NativeBuffer, uint> read)
    {
        uint bytes = read(new NativeBuffer(null, 0));
        if (bytes <= 2 || bytes > 1024 * 1024)
        {
            return string.Empty;
        }

        var chars = new char[bytes / 2];
        fixed (char* c = chars)
        {
            read(new NativeBuffer(c, bytes));
        }

        return new string(chars, 0, chars.Length - 1); // drop terminator
    }
}
