using System.Text.RegularExpressions;

namespace Papery.Core.Text;

/// <summary>Kinds of private detail that redaction can look for.</summary>
[Flags]
public enum SensitiveKind
{
    None = 0,
    Email = 1,
    Phone = 2,
    CardNumber = 4,
    BankAccount = 8,
    IdNumber = 16,
}

/// <summary>A stretch of text that looks like a private detail.</summary>
public readonly record struct SensitiveMatch(int Index, int Length, SensitiveKind Kind);

/// <summary>
/// Finds private details in a page's text for redaction to mark. Numbers with a check digit (cards, IBANs,
/// Aadhaar) are only taken when the check passes, so order numbers and the like aren't marked by mistake.
/// Marking a little too much is safer than missing something, so the patterns allow spaces and dashes inside.
/// </summary>
public static partial class SensitiveText
{
    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w.])\+?\(?\d[\d ()\-.]{7,}\d(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    // 13 to 19 digits, in groups split by single spaces or dashes.
    [GeneratedRegex(@"(?<![\d\-])\d(?:[ \-]?\d){12,18}(?![\d\-])", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();

    // Two letters, two check digits, then up to 30 letters and digits, often printed in groups of four.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    // US Social Security, UK National Insurance, Indian PAN and Aadhaar.
    [GeneratedRegex(@"(?<![\d\-])(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?![\d\-])", RegexOptions.CultureInvariant)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?!BG|GB|KN|NK|NT|TN|ZZ)[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D](?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NationalInsurancePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{3}[ABCFGHJLPT][A-Z]\d{4}[A-Z](?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"(?<![\d\-])[2-9]\d{3}[ \-]?\d{4}[ \-]?\d{4}(?![\d\-])", RegexOptions.CultureInvariant)]
    private static partial Regex AadhaarPattern();

    /// <summary>Every match of the chosen kinds, in the order they appear. Overlaps are left in; marking twice does no harm.</summary>
    public static IReadOnlyList<SensitiveMatch> Find(string text, SensitiveKind kinds)
    {
        var found = new List<SensitiveMatch>();
        void Add(Regex pattern, SensitiveKind kind, Func<string, bool>? valid = null)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (valid == null || valid(match.Value))
                {
                    found.Add(new SensitiveMatch(match.Index, match.Length, kind));
                }
            }
        }

        if (kinds.HasFlag(SensitiveKind.Email))
        {
            Add(EmailPattern(), SensitiveKind.Email);
        }

        if (kinds.HasFlag(SensitiveKind.Phone))
        {
            Add(PhonePattern(), SensitiveKind.Phone);
        }

        if (kinds.HasFlag(SensitiveKind.CardNumber))
        {
            Add(CardPattern(), SensitiveKind.CardNumber, value => PassesLuhn(Digits(value)));
        }

        if (kinds.HasFlag(SensitiveKind.BankAccount))
        {
            Add(IbanPattern(), SensitiveKind.BankAccount, IsIban);
        }

        if (kinds.HasFlag(SensitiveKind.IdNumber))
        {
            Add(SsnPattern(), SensitiveKind.IdNumber);
            Add(NationalInsurancePattern(), SensitiveKind.IdNumber);
            Add(PanPattern(), SensitiveKind.IdNumber);
            Add(AadhaarPattern(), SensitiveKind.IdNumber, value => PassesVerhoeff(Digits(value)));
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found;
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    /// <summary>The check every payment card number carries in its last digit.</summary>
    internal static bool PassesLuhn(string digits)
    {
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    /// <summary>IBANs carry two check digits: moved to the end and read as one number, the whole thing leaves 1 when divided by 97.</summary>
    internal static bool IsIban(string value)
    {
        string compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length is < 15 or > 34)
        {
            return false;
        }

        string moved = compact[4..] + compact[..4];
        int remainder = 0;
        foreach (char c in moved)
        {
            int number = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = number > 9 ? ((remainder * 100) + number) % 97 : ((remainder * 10) + number) % 97;
        }

        return remainder == 1;
    }

    private static readonly int[,] VerhoeffD =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 }, { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 },
        { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 }, { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 },
        { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 }, { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] VerhoeffP =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 }, { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 }, { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    /// <summary>The check Aadhaar numbers carry, which catches every single wrong digit and swapped pair.</summary>
    internal static bool PassesVerhoeff(string digits)
    {
        int check = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            check = VerhoeffD[check, VerhoeffP[i % 8, digits[digits.Length - 1 - i] - '0']];
        }

        return digits.Length == 12 && check == 0;
    }
}
