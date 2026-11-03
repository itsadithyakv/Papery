using System.Text;

namespace Papery.Core.Text;

/// <summary>Reads text files the way Notepad does, and tidies text for printing.</summary>
public static class PlainText
{
    /// <summary>
    /// The text in a file's bytes: UTF-8 or UTF-16 when the file says so or reads as it, otherwise the older
    /// Western encoding (Latin-1), so a file saved by an old program still opens instead of showing as boxes.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Text ready to lay out: one line ending (\n), tabs turned into spaces to the next stop, and control characters
    /// dropped (a font draws them as boxes). Form feeds (\f), which start a new page, are kept.
    /// </summary>
    public static string Tidy(string text, int tabSize = 4)
    {
        var result = new StringBuilder(text.Length);
        int column = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                c = '\n';
            }

            if (c is '\n' or '\f')
            {
                result.Append(c);
                column = 0;
            }
            else if (c == '\t')
            {
                int spaces = tabSize - (column % tabSize);
                result.Append(' ', spaces);
                column += spaces;
            }
            else if (!char.IsControl(c) && c != '﻿')
            {
                result.Append(c);
                column++;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Breaks text into lines no wider than <paramref name="columns"/> characters, at spaces where it can and mid-word
    /// where a word is longer than a line. Indents and blank lines are kept. A form feed comes back as a line of its own
    /// holding just "\f", so the caller can start a new page there.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string tidyText, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var lines = new List<string>();
        foreach (string paragraph in tidyText.Split('\n'))
        {
            string[] pages = paragraph.Split('\f');
            for (int p = 0; p < pages.Length; p++)
            {
                if (p > 0)
                {
                    lines.Add("\f");
                }

                if (p > 0 && pages[p].Length == 0)
                {
                    continue; // a form feed at the end of a line doesn't add an empty line after it
                }

                WrapOne(pages[p], columns, lines);
            }
        }

        return lines;
    }

    private static void WrapOne(string line, int columns, List<string> into)
    {
        while (line.Length > columns)
        {
            int cut = line.LastIndexOf(' ', columns);
            int indent = line.Length - line.TrimStart(' ').Length;
            if (cut <= indent)
            {
                into.Add(line[..columns]);
                line = line[columns..];
            }
            else
            {
                into.Add(line[..cut].TrimEnd(' '));
                line = line[(cut + 1)..];
            }
        }

        into.Add(line);
    }
}
