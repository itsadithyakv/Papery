using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace Papery.Pdf.Editing;

/// <summary>What a certified signature says about itself.</summary>
public sealed record PdfSignatureRequest(X509Certificate2 Certificate, string? Reason = null, string? Location = null);

/// <summary>
/// Adds a real, certificate-based digital signature. Unlike a picture of a signature, it proves who signed and shows
/// if the file changes afterwards. Whether a viewer trusts it depends on who issued the certificate.
/// </summary>
internal static class PdfSigner
{
    /// <summary>Certificates in the user's Windows store that can sign and have their private key here.</summary>
    public static IReadOnlyList<X509Certificate2> UsableCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var now = DateTime.Now;
        return store.Certificates.Cast<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter >= now && CanSign(c))
            .OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool CanSign(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage == null || (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
    }

    public static async Task SignAsync(PdfiumThread thread, PdfInputFile input, PdfSignatureRequest request, string outputPath, CancellationToken cancellationToken)
    {
        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "sign", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string final = Path.Combine(work, "signed.pdf");
            int pages = await Task.Run(async () =>
            {
                using var doc = input.Password is null
                    ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Modify)
                    : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Modify);
                int count = doc.PageCount;
                _ = DigitalSignatureHandler.ForDocument(doc, new CmsSignatureMaker(request.Certificate), new DigitalSignatureOptions
                {
                    Reason = request.Reason ?? string.Empty,
                    Location = request.Location ?? string.Empty,
                    AppName = "Papery",
                });
                await using var stream = new FileStream(final, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                await doc.SaveAsync(stream, closeStream: false).ConfigureAwait(false);
                return count;
            }, cancellationToken).ConfigureAwait(false);

            await PdfCompressor.CommitAsync(thread, final, input.Password, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or PdfReaderException or System.Security.Cryptography.CryptographicException)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, input.Path, ex);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    private sealed class CmsSignatureMaker(X509Certificate2 certificate) : IDigitalSigner
    {
        public string CertificateName { get; } = certificate.GetNameInfo(X509NameType.SimpleName, false);

        public Task<int> GetSignatureSizeAsync()
        {
            // Measured with a real signature, with room to spare because each one can vary a little.
            byte[] sample = Make(new byte[64]);
            return Task.FromResult(sample.Length + 1024);
        }

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            // PDFsharp's stream covers only the signed parts and can't seek, so it's read whole in one go.
            byte[] content = new byte[stream.Length];
            int total = 0;
            while (total < content.Length)
            {
                int read = stream.Read(content, total, content.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return Task.FromResult(Make(content));
        }

        private byte[] Make(byte[] content)
        {
            var signed = new SignedCms(new ContentInfo(content), detached: true);
            signed.ComputeSignature(new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly, DigestAlgorithm = new System.Security.Cryptography.Oid("2.16.840.1.101.3.4.2.1") });
            return signed.Encode();
        }
    }
}
