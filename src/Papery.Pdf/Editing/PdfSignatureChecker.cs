using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>What checking one digital signature found.</summary>
/// <param name="Signer">Who the certificate names, or null if it can't be read.</param>
/// <param name="CanCheck">False for an old or unusual signature type Papery can't verify.</param>
/// <param name="Intact">The part that was signed hasn't changed.</param>
/// <param name="CoversWholeFile">False when more was added after signing, such as a later signature or a change.</param>
/// <param name="TrustedSigner">Windows trusts the certificate. If not, the name in it is only what the signer claimed.</param>
public sealed record PdfSignatureCheck(string? Signer, DateTimeOffset? SignedAt, string? Reason, bool CanCheck, bool Intact, bool CoversWholeFile, bool TrustedSigner);

/// <summary>
/// Checks a PDF's digital signatures: are the bytes unchanged since signing, and is the signer's certificate trusted?
/// Stays offline, so it can't tell if a certificate was revoked later, and the UI says so.
/// </summary>
internal static class PdfSignatureChecker
{
    private static readonly string[] Checkable = ["adbe.pkcs7.detached", "ETSI.CAdES.detached"];

    public static async Task<IReadOnlyList<PdfSignatureCheck>> CheckAsync(PdfiumThread thread, PdfInputFile input, CancellationToken cancellationToken)
    {
        var found = await thread.InvokeAsync(() => Read(input), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            return [];
        }

        byte[] file = await File.ReadAllBytesAsync(input.Path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => found.Select(f => Check(f, file)).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static unsafe List<Found> Read(PdfInputFile input)
    {
        using var doc = RawPdf.Load(input.Path, input.Password);
        var found = new List<Found>();
        int count = PdfiumNative.FPDF_GetSignatureCount(doc.Handle);
        for (int i = 0; i < count; i++)
        {
            nint signature = PdfiumNative.FPDF_GetSignatureObject(doc.Handle, i);
            if (signature == 0)
            {
                continue;
            }

            uint size = PdfiumNative.FPDFSignatureObj_GetContents(signature, null, 0);
            byte[] contents = new byte[size];
            fixed (byte* c = contents)
            {
                _ = PdfiumNative.FPDFSignatureObj_GetContents(signature, c, size);
            }

            uint parts = PdfiumNative.FPDFSignatureObj_GetByteRange(signature, null, 0);
            int[] range = new int[parts];
            fixed (int* r = range)
            {
                _ = PdfiumNative.FPDFSignatureObj_GetByteRange(signature, r, parts);
            }

            found.Add(new Found(contents, range, Ascii(signature, PdfiumNative.FPDFSignatureObj_GetSubFilter), Ascii(signature, PdfiumNative.FPDFSignatureObj_GetTime), Reason(signature)));
        }

        return found;
    }

    private unsafe delegate uint ReadAscii(nint signature, byte* buffer, uint length);

    private static unsafe string Ascii(nint signature, ReadAscii read)
    {
        uint size = read(signature, null, 0);
        if (size <= 1)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[size];
        fixed (byte* b = bytes)
        {
            _ = read(signature, b, size);
        }

        return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
    }

    private static unsafe string? Reason(nint signature)
    {
        uint size = PdfiumNative.FPDFSignatureObj_GetReason(signature, null, 0);
        if (size <= 2)
        {
            return null;
        }

        char[] text = new char[size / 2];
        fixed (char* t = text)
        {
            _ = PdfiumNative.FPDFSignatureObj_GetReason(signature, t, size);
        }

        return new string(text).TrimEnd('\0');
    }

    private static PdfSignatureCheck Check(Found found, byte[] file)
    {
        var claimedTime = ParseTime(found.Time);
        if (!Checkable.Contains(found.SubFilter, StringComparer.Ordinal) || found.Contents.Length == 0)
        {
            return new PdfSignatureCheck(null, claimedTime, found.Reason, false, false, false, false);
        }

        // [start, length, start, length], with only the signature itself in the gap between them.
        int[] r = found.Range;
        bool sane = r.Length == 4 && r[0] == 0 && r[1] > 0 && r[2] >= r[1] && r[3] >= 0 && (long)r[2] + r[3] <= file.Length;
        if (!sane)
        {
            return new PdfSignatureCheck(null, claimedTime, found.Reason, true, false, false, false);
        }

        byte[] covered = new byte[r[1] + r[3]];
        Buffer.BlockCopy(file, r[0], covered, 0, r[1]);
        Buffer.BlockCopy(file, r[2], covered, r[1], r[3]);
        bool gapIsOnlyTheSignature = OnlySignatureBetween(file, r[1], r[2]);
        bool coversAll = (long)r[2] + r[3] == file.Length;

        var signed = new SignedCms(new ContentInfo(covered), detached: true);
        try
        {
            signed.Decode(WithoutPadding(found.Contents));
        }
        catch (CryptographicException)
        {
            return new PdfSignatureCheck(null, claimedTime, found.Reason, false, false, coversAll, false);
        }

        var certificate = signed.SignerInfos.Count > 0 ? signed.SignerInfos[0].Certificate : null;
        string? signer = certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        bool intact = gapIsOnlyTheSignature;
        if (intact)
        {
            try
            {
                signed.CheckSignature(verifySignatureOnly: true);
            }
            catch (CryptographicException)
            {
                intact = false;
            }
        }

        var signedAt = SigningTime(signed) ?? claimedTime;
        return new PdfSignatureCheck(signer, signedAt, found.Reason, true, intact, coversAll, certificate != null && Trusted(certificate, signed.Certificates));
    }

    /// <summary>The signature sits in the file as hex between angle brackets, and nothing else may hide in that gap.</summary>
    private static bool OnlySignatureBetween(byte[] file, int from, int to)
    {
        if (to - from < 2 || file[from] != '<' || file[to - 1] != '>')
        {
            return false;
        }

        for (int i = from + 1; i < to - 1; i++)
        {
            if (!Uri.IsHexDigit((char)file[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Space is reserved for the signature in advance and padded with zeros, which the decoder must not see.</summary>
    private static byte[] WithoutPadding(byte[] contents)
    {
        if (contents.Length < 2 || contents[0] != 0x30)
        {
            return contents;
        }

        int length;
        int header;
        if (contents[1] < 0x80)
        {
            length = contents[1];
            header = 2;
        }
        else
        {
            int bytes = contents[1] & 0x7F;
            if (bytes is 0 or > 4 || contents.Length < 2 + bytes)
            {
                return contents;
            }

            length = 0;
            for (int i = 0; i < bytes; i++)
            {
                length = (length << 8) | contents[2 + i];
            }

            header = 2 + bytes;
        }

        return length > 0 && header + length <= contents.Length ? contents[..(header + length)] : contents;
    }

    private static DateTimeOffset? SigningTime(SignedCms signed)
    {
        if (signed.SignerInfos.Count == 0)
        {
            return null;
        }

        foreach (var attribute in signed.SignerInfos[0].SignedAttributes)
        {
            foreach (var value in attribute.Values)
            {
                if (value is Pkcs9SigningTime time)
                {
                    return new DateTimeOffset(time.SigningTime);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checked as of today. The time inside a signature is only what the signer typed, so going by it would let a certificate
    /// that ran out years ago pass with a backdated signature.
    /// </summary>
    private static bool Trusted(X509Certificate2 certificate, X509Certificate2Collection others)
    {
        if (!MaySignDocuments(certificate))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // that would need the internet
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ExtraStore.AddRange(others);
        return chain.Build(certificate);
    }

    /// <summary>A website's certificate is trusted by Windows too, but only to run a website. No stated use means any use.</summary>
    private static bool MaySignDocuments(X509Certificate2 certificate)
    {
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usages == null || usages.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value is
            "2.5.29.37.0" // any use
            or "1.3.6.1.5.5.7.3.36" // document signing
            or "1.3.6.1.4.1.311.10.3.12" // Microsoft document signing
            or "1.2.840.113583.1.1.5" // Adobe PDF signing
            or "1.3.6.1.5.5.7.3.4"); // email, which many personal signing certificates use
    }

    /// <summary>PDF dates look like D:20240131143000+05'30'. Only the date and time matter here.</summary>
    private static DateTimeOffset? ParseTime(string text)
    {
        string digits = text.StartsWith("D:", StringComparison.Ordinal) ? text[2..] : text;
        return digits.Length >= 14 && DateTime.TryParseExact(digits[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? new DateTimeOffset(when)
            : null;
    }

    private sealed record Found(byte[] Contents, int[] Range, string SubFilter, string Time, string? Reason);
}
