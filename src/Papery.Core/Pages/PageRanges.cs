using System.Globalization;

namespace Papery.Core.Pages;

/// <summary>Page ranges typed by people: "1-3, 5, 8-". Used by printing, merge and split.</summary>
public static class PageRanges
{
    private const string EmptyMessage = "Enter pages, for example 1-3, 5.";

    /// <summary>
    /// Parses a page range such as "1-3, 5, 8-" (8 to the end) or "-4" (start to 4).
    /// Pages are 1-based in the text and 0-based in the result. Duplicates are kept,
    /// so "1, 1" means page one twice, like other apps.
    /// </summary>
    /// <returns>False with a friendly <paramref name="error"/> if the text can't be understood.</returns>
    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages, out string? error, string emptyMessage = EmptyMessage)
    {
        var result = new List<int>();
        pages = result;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = emptyMessage;
            return false;
        }

        foreach (string rawPart in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.Replace(" ", string.Empty, StringComparison.Ordinal).Replace('–', '-');
            int dash = part.IndexOf('-', StringComparison.Ordinal);
            int from, to;
            if (dash < 0)
            {
                if (!TryPage(part, out from))
                {
                    error = $"\"{rawPart}\" isn't a page number.";
                    return false;
                }

                to = from;
            }
            else
            {
                string left = part[..dash], right = part[(dash + 1)..];
                if (left.Length == 0 && right.Length == 0)
                {
                    error = "Add a page number before or after the dash, for example 3-7.";
                    return false;
                }

                from = left.Length == 0 ? 1 : TryPage(left, out int l) ? l : -1;
                to = right.Length == 0 ? pageCount : TryPage(right, out int r) ? r : -1;
                if (from < 0 || to < 0)
                {
                    error = $"\"{rawPart}\" isn't a page range.";
                    return false;
                }
            }

            if (from < 1 || to > pageCount || from > pageCount)
            {
                error = $"This document has pages 1 to {pageCount}.";
                return false;
            }

            if (from > to)
            {
                error = $"\"{rawPart}\" goes backwards. Write it as {to}-{from}.";
                return false;
            }

            for (int p = from; p <= to; p++)
            {
                result.Add(p - 1);
            }
        }

        if (result.Count == 0)
        {
            error = emptyMessage;
            return false;
        }

        return true;

        static bool TryPage(string s, out int page) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;
    }

    /// <summary>
    /// Parses each comma-separated part as its own group: "1-3, 4-8, 9" gives three groups.
    /// Used by Split, where every group becomes one file.
    /// </summary>
    public static bool TryParseGroups(string? text, int pageCount, out IReadOnlyList<IReadOnlyList<int>> groups, out string? error)
    {
        var result = new List<IReadOnlyList<int>>();
        groups = result;
        error = null;
        foreach (string part in (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(part, pageCount, out var pages, out error))
            {
                return false;
            }

            result.Add(pages);
        }

        if (result.Count == 0)
        {
            error = "Enter the parts, for example 1-3, 4-10.";
            return false;
        }

        return true;
    }

    /// <summary>Formats pages (0-based) compactly for people: [0,1,2,4] gives "1-3, 5".</summary>
    public static string Format(IEnumerable<int> pages)
    {
        var parts = new List<string>();
        int? start = null, previous = null;
        foreach (int page in pages)
        {
            if (previous is int p && page == p + 1)
            {
                previous = page;
                continue;
            }

            Flush();
            start = previous = page;
        }

        Flush();
        return string.Join(", ", parts);

        void Flush()
        {
            if (start is int s && previous is int e)
            {
                parts.Add(s == e ? (s + 1).ToString(CultureInfo.CurrentCulture) : $"{s + 1}-{e + 1}");
            }
        }
    }
}
