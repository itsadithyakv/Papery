using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Papery.Services.Printing;

/// <summary>
/// The small part of the Windows printing API Papery needs (winspool + GDI).
/// A DEVMODE is the printer driver's settings blob (paper, orientation, duplex...);
/// we keep it as raw bytes and only touch the few documented fields we care about.
/// </summary>
internal static unsafe partial class Win32Printing
{
    // DEVMODEW field offsets (wingdi.h). The struct layout has been fixed since Windows 2000.
    private const int OffsetSize = 68;          // dmSize
    private const int OffsetDriverExtra = 70;   // dmDriverExtra
    private const int OffsetFields = 72;        // dmFields
    private const int OffsetOrientation = 76;   // dmOrientation
    private const int OffsetCopies = 86;        // dmCopies
    private const int OffsetColor = 92;         // dmColor
    private const int OffsetDuplex = 94;        // dmDuplex
    private const int OffsetCollate = 100;      // dmCollate

    private const uint DM_ORIENTATION = 0x00000001;
    private const uint DM_COPIES = 0x00000100;
    private const uint DM_COLOR = 0x00000800;
    private const uint DM_DUPLEX = 0x00001000;
    private const uint DM_COLLATE = 0x00008000;

    private const uint DM_OUT_BUFFER = 2;
    private const uint DM_IN_PROMPT = 4;
    private const uint DM_IN_BUFFER = 8;

    private const uint PRINTER_ENUM_LOCAL = 0x2;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x4;

    // GetDeviceCaps indexes
    public const int HORZRES = 8;
    public const int VERTRES = 10;
    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;

    public enum Duplex : short
    {
        OneSided = 1,
        LongEdge = 2,
        ShortEdge = 3,
    }

    public static IReadOnlyList<string> GetPrinters()
    {
        uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrintersW(flags, 0, 4, 0, 0, out uint needed, out _);
        if (needed == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrintersW(flags, 0, 4, buffer, needed, out _, out uint count))
            {
                return [];
            }

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                // PRINTER_INFO_4W: { LPWSTR pPrinterName; LPWSTR pServerName; DWORD Attributes; }
                nint entry = buffer + (i * ((2 * nint.Size) + nint.Size));
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry));
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }

            return names.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? GetDefaultPrinter()
    {
        uint size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size == 0)
        {
            return null;
        }

        var chars = new char[size];
        fixed (char* c = chars)
        {
            return GetDefaultPrinterW(c, ref size) ? new string(c) : null;
        }
    }

    /// <summary>The driver's default settings for a printer, or null if it can't be read.</summary>
    public static byte[]? GetDefaultDevMode(string printer) => CallDocumentProperties(0, printer, null, prompt: false);

    /// <summary>Shows the printer driver's own "Properties" window. Returns the new settings, or null if cancelled.</summary>
    public static byte[]? ShowPrinterProperties(nint ownerWindow, string printer, byte[] devMode) =>
        CallDocumentProperties(ownerWindow, printer, devMode, prompt: true);

    /// <summary>Writes Papery's choices into a copy of a DEVMODE.</summary>
    public static byte[] ApplySettings(byte[] devMode, bool landscape, short copies, bool collate, bool color, Duplex? duplex)
    {
        var d = (byte[])devMode.Clone();
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(OffsetFields));
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(OffsetOrientation), (short)(landscape ? 2 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(OffsetCopies), Math.Max((short)1, copies));
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(OffsetCollate), (short)(collate ? 1 : 0));
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(OffsetColor), (short)(color ? 2 : 1));
        fields |= DM_ORIENTATION | DM_COPIES | DM_COLLATE | DM_COLOR;
        if (duplex is Duplex dx)
        {
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(OffsetDuplex), (short)dx);
            fields |= DM_DUPLEX;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(OffsetFields), fields);
        return d;
    }

    public static nint CreatePrinterDC(string printer, byte[] devMode)
    {
        fixed (byte* dm = devMode)
        {
            return CreateDCW("WINSPOOL", printer, null, (nint)dm);
        }
    }

    public static bool StartDocument(nint hdc, string title)
    {
        nint name = Marshal.StringToHGlobalUni(title);
        try
        {
            var info = new DOCINFOW { cbSize = sizeof(DOCINFOW), lpszDocName = name };
            return StartDocW(hdc, &info) > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static byte[]? CallDocumentProperties(nint owner, string printer, byte[]? input, bool prompt)
    {
        if (!OpenPrinterW(printer, out nint handle, 0))
        {
            return null;
        }

        try
        {
            int size = DocumentPropertiesW(owner, handle, printer, 0, 0, 0);
            if (size <= 0)
            {
                return null;
            }

            var output = new byte[Math.Max(size, input?.Length ?? 0)];
            fixed (byte* o = output)
            fixed (byte* i = input)
            {
                uint mode = DM_OUT_BUFFER | (input != null ? DM_IN_BUFFER : 0) | (prompt ? DM_IN_PROMPT : 0);
                int result = DocumentPropertiesW(owner, handle, printer, (nint)o, (nint)i, mode);
                if (result != 1) // IDOK
                {
                    return null;
                }
            }

            int realSize = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(OffsetSize))
                         + BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(OffsetDriverExtra));
            return realSize > 0 && realSize <= output.Length ? output[..realSize] : output;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOCINFOW
    {
        public int cbSize;
        public nint lpszDocName;
        public nint lpszOutput;
        public nint lpszDatatype;
        public uint fwType;
    }

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrintersW(uint flags, nint name, uint level, nint buffer, uint bufferSize, out uint needed, out uint returned);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDefaultPrinterW(char* buffer, ref uint size);

    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string name, out nint handle, nint defaults);

    [LibraryImport("winspool.drv")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint handle);

    [LibraryImport("winspool.drv", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DocumentPropertiesW(nint owner, nint printer, string deviceName, nint output, nint input, uint mode);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDCW(string driver, string device, string? output, nint devMode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(nint hdc, int index);

    [LibraryImport("gdi32.dll")]
    private static partial int StartDocW(nint hdc, DOCINFOW* info);

    [LibraryImport("gdi32.dll")]
    public static partial int StartPage(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int EndPage(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int EndDoc(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial int AbortDoc(nint hdc);
}
