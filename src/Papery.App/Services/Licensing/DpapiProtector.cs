using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Papery.Core.Licensing;

namespace Papery.Services.Licensing;

/// <summary>
/// Encrypts the local "Pro was bought" record with Windows DPAPI (CryptProtectData), tied to the
/// current Windows account. Another account, or a copy on another PC, can't read it.
/// Uses Windows' own crypt32.dll: no extra library.
/// </summary>
internal sealed partial class DpapiProtector : ISecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // Extra "salt" so this blob is Papery's own and can't be swapped with another app's DPAPI data.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Papery.Pro.Licence.v1");

    public byte[] Protect(byte[] data) => Transform(data, protect: true);

    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { cbData = data.Length, pbData = (nint)input };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = (nint)entropy };
            bool ok = protect
                ? CryptProtectData(ref inBlob, 0, ref entropyBlob, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
            {
                throw new CryptographicException($"DPAPI failed ({Marshal.GetLastPInvokeError()}).");
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
