using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Papery.Pdf.Editing;

/// <summary>
/// What comment and form data files have in common: XFDF is Adobe's XML for both, and files come from
/// anyone, so reading is strict about size and never follows a DTD or fetches anything.
/// </summary>
internal static partial class Xfdf
{
    public const string Namespace = "http://ns.adobe.com/xfdf/";

    /// <summary>Far bigger than any real comments or answers file; a bigger one is refused before it's read.</summary>
    public const long LargestFile = 32L * 1024 * 1024;

    // Longer words than anyone types into a comment or field; the rest is cut off rather than slowing everything down.
    public const int LongestText = 100_000;

    public static XmlReaderSettings ReaderSettings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, // entity tricks can't start without a DTD
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = LargestFile,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    public static XmlWriterSettings WriterSettings => new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = true,
        NewLineHandling = NewLineHandling.Entitize, // keeps the \r Acrobat uses in multi-line answers
        CloseOutput = false,
    };

    /// <summary>Opens a file to read, refusing ones too big to be genuine.</summary>
    public static FileStream OpenChecked(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (stream.Length > LargestFile)
        {
            stream.Dispose();
            throw new InvalidDataException("That file is too big to be comments or form answers.");
        }

        return stream;
    }

    /// <summary>Writes a file through a hidden temp copy, so a failure never leaves half of one.</summary>
    public static void WriteSafely(string outputPath, Action<Stream> write)
    {
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.SetLength(0);
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            SafeFile.Commit(temp, outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEditException(PdfEditError.CannotWrite, outputPath, ex);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    /// <summary>Text with the characters XML can't hold taken out (PDF strings can contain any of them).</summary>
    public static string Clean(string text)
    {
        if (text.Length > LongestText)
        {
            text = text[..LongestText];
        }

        StringBuilder? clean = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (pair || XmlConvert.IsXmlChar(c))
            {
                clean?.Append(c);
                if (pair)
                {
                    clean?.Append(text[++i]);
                }

                continue;
            }

            clean ??= new StringBuilder(text, 0, i, text.Length);
        }

        return clean?.ToString() ?? text;
    }

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A number from the file, or null when it isn't a sensible page measurement.</summary>
    public static double? ParseNumber(ReadOnlySpan<char> text)
    {
        const double Furthest = 1_000_000; // PDF pages stop well short of this
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && Math.Abs(value) <= Furthest
            ? value
            : null;
    }

    /// <summary>Numbers separated by commas, semicolons or spaces, or null if any isn't a number or there are too many.</summary>
    public static List<double>? ParseNumbers(string? text, int most)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var result = new List<double>();
        foreach (var range in text.AsSpan().SplitAny(",; \t\r\n"))
        {
            var piece = text.AsSpan()[range];
            if (piece.IsEmpty)
            {
                continue;
            }

            if (result.Count >= most || ParseNumber(piece) is not { } value)
            {
                return null;
            }

            result.Add(value);
        }

        return result;
    }

    public static string Colour(PdfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static PdfColor? ParseColour(string? text)
    {
        if (text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return new PdfColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return null;
    }

    /// <summary>The font size and text colour in a PDF default appearance such as "1 0 0 rg /Helv 12 Tf".</summary>
    public static (float? Size, PdfColor? Colour) ParseAppearance(string? appearance)
    {
        if (string.IsNullOrEmpty(appearance) || appearance.Length > 1000)
        {
            return (null, null);
        }

        float? size = SizeInAppearance().Match(appearance) is { Success: true } s
            && float.TryParse(s.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out float points) && points is >= 1 and <= 400
            ? points
            : null;
        PdfColor? colour = null;
        if (ColourInAppearance().Match(appearance) is { Success: true } c)
        {
            static byte Part(Group g) => (byte)Math.Round(Math.Clamp(double.Parse(g.ValueSpan, CultureInfo.InvariantCulture), 0, 1) * 255);
            colour = new PdfColor(Part(c.Groups[1]), Part(c.Groups[2]), Part(c.Groups[3]));
        }
        else if (GreyInAppearance().Match(appearance) is { Success: true } g)
        {
            byte grey = (byte)Math.Round(Math.Clamp(double.Parse(g.Groups[1].ValueSpan, CultureInfo.InvariantCulture), 0, 1) * 255);
            colour = new PdfColor(grey, grey, grey);
        }

        return (size, colour);
    }

    public static string Appearance(PdfColor c, float size) =>
        $"{Number(c.R / 255.0)} {Number(c.G / 255.0)} {Number(c.B / 255.0)} rg /Helv {Number(size)} Tf";

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s+Tf", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex SizeInAppearance();

    [GeneratedRegex(@"(\d*\.?\d+)\s+(\d*\.?\d+)\s+(\d*\.?\d+)\s+rg", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex ColourInAppearance();

    [GeneratedRegex(@"(\d*\.?\d+)\s+g(?:\s|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex GreyInAppearance();
}
