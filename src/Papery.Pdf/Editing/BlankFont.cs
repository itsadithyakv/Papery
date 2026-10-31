using System.Text;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>
/// A tiny TrueType font with one empty glyph, for invisible words. Its "to Unicode" table says every code
/// simply is that Unicode character, so search and copy read any alphabet, at a few hundred bytes in the file.
/// </summary>
internal static class BlankFont
{
    private const int UnitsPerEm = 1000;

    private static readonly byte[] ToUnicode = Encoding.ASCII.GetBytes(
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "1 beginbfrange\n<0000> <FFFF> <0000>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\0");

    // Identity: every code draws glyph 0, the blank one. Two bytes for each of the first two codes is all a reader needs to see it's valid.
    private static readonly byte[] CodesToGlyphs = new byte[2];

    public static unsafe nint Load(nint document)
    {
        byte[] font = Build();
        fixed (byte* data = font, cmap = ToUnicode, map = CodesToGlyphs)
        {
            return PdfiumNative.FPDFText_LoadCidType2Font(document, data, (uint)font.Length, cmap, map, (uint)CodesToGlyphs.Length);
        }
    }

    /// <summary>Builds the font file. Public to tests, which check that real font software accepts it.</summary>
    internal static byte[] Build()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = Os2(),
            ["cmap"] = Cmap(),
            ["glyf"] = [],
            ["head"] = Head(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Be(16, UnitsPerEm, 16, 0),
            ["loca"] = Be(16, 0, 16, 0),
            ["maxp"] = Be(32, 0x00010000, 16, 1, 16, 0, 16, 0, 16, 0, 16, 0, 16, 1, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0),
            ["name"] = Name(),
            ["post"] = Be(32, 0x00030000, 32, 0, 16, 0, 16, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0),
        };

        int entries = tables.Count;
        int log = (int)Math.Floor(Math.Log2(entries));
        using var file = new MemoryStream();
        Write(file, Be(32, 0x00010000, 16, entries, 16, (1 << log) * 16, 16, log, 16, (entries * 16) - ((1 << log) * 16)));

        int offset = 12 + (16 * entries);
        var bodies = new List<byte[]>();
        foreach (var (tag, data) in tables)
        {
            Write(file, Encoding.ASCII.GetBytes(tag));
            Write(file, Be(32, CheckSum(data), 32, offset, 32, data.Length));
            int padded = (data.Length + 3) & ~3;
            offset += padded;
            bodies.Add(data);
        }

        foreach (byte[] data in bodies)
        {
            Write(file, data);
            Write(file, new byte[((data.Length + 3) & ~3) - data.Length]);
        }

        byte[] all = file.ToArray();

        // The file's overall check, which goes in the head table.
        long adjustment = 0xB1B0AFBAL - CheckSum(all);
        int head = HeadOffset(all, entries);
        all[head + 8] = (byte)(adjustment >> 24);
        all[head + 9] = (byte)(adjustment >> 16);
        all[head + 10] = (byte)(adjustment >> 8);
        all[head + 11] = (byte)adjustment;
        return all;
    }

    private static int HeadOffset(byte[] file, int entries)
    {
        for (int i = 0; i < entries; i++)
        {
            int at = 12 + (16 * i);
            if (Encoding.ASCII.GetString(file, at, 4) == "head")
            {
                return (file[at + 8] << 24) | (file[at + 9] << 16) | (file[at + 10] << 8) | file[at + 11];
            }
        }

        throw new InvalidOperationException("The font has no head table.");
    }

    private static byte[] Head() => Be(
        32, 0x00010000, 32, 0x00010000, 32, 0, 32, 0x5F0F3CF5, 16, 0x000B, 16, UnitsPerEm,
        32, 0, 32, 0xE0000000L, 32, 0, 32, 0xE0000000L,
        16, 0, 16, 0, 16, UnitsPerEm, 16, UnitsPerEm,
        16, 0, 16, 8, 16, 2, 16, 0, 16, 0);

    private static byte[] Hhea() => Be(
        32, 0x00010000, 16, 800, 16, -200 & 0xFFFF, 16, 0, 16, UnitsPerEm,
        16, 0, 16, 0, 16, UnitsPerEm, 16, 1, 16, 0, 16, 0,
        16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 1);

    private static byte[] Os2() => Be(
        16, 1, 16, UnitsPerEm / 2, 16, 400, 16, 5, 16, 0,
        16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0, 16, 0,
        16, 0, 32, 0, 32, 0, 16, 0,
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0x50415052, 16, 0x0040, 16, 0x0020, 16, 0x0020,
        16, 800, 16, -200 & 0xFFFF, 16, 0, 16, 800, 16, 200, 32, 1, 32, 0);

    private static byte[] Cmap() => Be(
        16, 0, 16, 1, 16, 3, 16, 1, 32, 12,
        16, 4, 16, 24, 16, 0, 16, 2, 16, 2, 16, 0, 16, 0,
        16, 0xFFFF, 16, 0, 16, 0xFFFF, 16, 1, 16, 0);

    private static byte[] Name()
    {
        byte[] text = Encoding.BigEndianUnicode.GetBytes("PaperyBlank");
        using var name = new MemoryStream();
        int[] ids = [1, 4, 6];
        Write(name, Be(16, 0, 16, ids.Length, 16, 6 + (12 * ids.Length)));
        foreach (int id in ids)
        {
            Write(name, Be(16, 3, 16, 1, 16, 0x0409, 16, id, 16, text.Length, 16, 0));
        }

        Write(name, text);
        return name.ToArray();
    }

    /// <summary>Big-endian numbers, given as pairs of bit size and value.</summary>
    private static byte[] Be(params long[] pairs)
    {
        using var bytes = new MemoryStream();
        for (int i = 0; i < pairs.Length; i += 2)
        {
            long value = pairs[i + 1];
            for (int shift = (int)pairs[i] - 8; shift >= 0; shift -= 8)
            {
                bytes.WriteByte((byte)(value >> shift));
            }
        }

        return bytes.ToArray();
    }

    private static void Write(Stream to, byte[] data) => to.Write(data, 0, data.Length);

    private static long CheckSum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : (uint)0);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }
}
