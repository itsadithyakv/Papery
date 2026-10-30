namespace Papery.Pdf.Editing;

/// <summary>
/// A plain baseline JPEG encoder, in colour (with half-size colour, as cameras do) or grey. PDFium can
/// only store new pictures losslessly, which would make photos bigger, so compression brings its own.
/// </summary>
internal static class JpegEncoder
{
    private static readonly byte[] LumaQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChromaQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly double[] Aan = [1, 1.387039845, 1.306562965, 1.175875602, 1, 0.785694958, 0.541196100, 0.275899379];

    private static readonly byte[] DcLumaBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChromaBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcLumaBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcChromaBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] AcLumaValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static readonly byte[] AcChromaValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    /// <param name="quality">1 (smallest) to 100 (best).</param>
    public static byte[] Encode(PdfBitmap picture, int quality, bool grey)
    {
        int width = picture.Width, height = picture.Height;
        int scale = quality < 50 ? 5000 / Math.Max(1, quality) : 200 - (2 * Math.Min(100, quality));
        byte[] lumaQ = Scaled(LumaQuant, scale), chromaQ = Scaled(ChromaQuant, scale);
        double[] lumaDiv = Divisors(lumaQ), chromaDiv = Divisors(chromaQ);

        // Planes, with half-size colour.
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        float[] y = new float[width * height];
        float[] cb = grey ? [] : new float[cw * ch];
        float[] cr = grey ? [] : new float[cw * ch];
        for (int row = 0; row < height; row++)
        {
            int at = row * picture.Stride;
            for (int col = 0; col < width; col++, at += 4)
            {
                float b = picture.Pixels[at], g = picture.Pixels[at + 1], r = picture.Pixels[at + 2];
                y[(row * width) + col] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128;
                if (!grey)
                {
                    int c = ((row / 2) * cw) + (col / 2);
                    cb[c] += ((-0.168736f * r) - (0.331264f * g) + (0.5f * b)) / 4;
                    cr[c] += ((0.5f * r) - (0.418688f * g) - (0.081312f * b)) / 4;
                }
            }
        }

        if (!grey && (width % 2 == 1 || height % 2 == 1))
        {
            // Colour samples on the far edges only collected half their pixels.
            for (int row = 0; row < ch; row++)
            {
                for (int col = 0; col < cw; col++)
                {
                    bool shortX = width % 2 == 1 && col == cw - 1, shortY = height % 2 == 1 && row == ch - 1;
                    float boost = (shortX ? 2 : 1) * (shortY ? 2 : 1);
                    cb[(row * cw) + col] *= boost;
                    cr[(row * cw) + col] *= boost;
                }
            }
        }

        using var output = new MemoryStream(Math.Max(1024, width * height / 8));
        WriteHeaders(output, width, height, grey, lumaQ, chromaQ);

        var dcLuma = Codes(DcLumaBits, DcValues);
        var acLuma = Codes(AcLumaBits, AcLumaValues);
        var dcChroma = Codes(DcChromaBits, DcValues);
        var acChroma = Codes(AcChromaBits, AcChromaValues);
        var bits = new BitWriter(output);
        float[] block = new float[64];
        int dcY = 0, dcCb = 0, dcCr = 0;
        int mcu = grey ? 8 : 16;
        for (int top = 0; top < height; top += mcu)
        {
            for (int left = 0; left < width; left += mcu)
            {
                for (int sub = 0; sub < (grey ? 1 : 4); sub++)
                {
                    Load(y, width, height, left + (sub % 2 * 8), top + (sub / 2 * 8), block);
                    dcY = EncodeBlock(bits, block, lumaDiv, dcY, dcLuma, acLuma);
                }

                if (!grey)
                {
                    Load(cb, cw, ch, left / 2, top / 2, block);
                    dcCb = EncodeBlock(bits, block, chromaDiv, dcCb, dcChroma, acChroma);
                    Load(cr, cw, ch, left / 2, top / 2, block);
                    dcCr = EncodeBlock(bits, block, chromaDiv, dcCr, dcChroma, acChroma);
                }
            }
        }

        bits.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    private static byte[] Scaled(byte[] table, int scale)
    {
        byte[] result = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            result[i] = (byte)Math.Clamp(((table[i] * scale) + 50) / 100, 1, 255);
        }

        return result;
    }

    private static double[] Divisors(byte[] quant)
    {
        double[] result = new double[64];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                result[(row * 8) + col] = quant[(row * 8) + col] * Aan[row] * Aan[col] * 8;
            }
        }

        return result;
    }

    /// <summary>Copies an 8x8 block, repeating the last row and column past the picture's edge.</summary>
    private static void Load(float[] plane, int width, int height, int left, int top, float[] block)
    {
        for (int row = 0; row < 8; row++)
        {
            int from = Math.Min(top + row, height - 1) * width;
            for (int col = 0; col < 8; col++)
            {
                block[(row * 8) + col] = plane[from + Math.Min(left + col, width - 1)];
            }
        }
    }

    private static int EncodeBlock(BitWriter bits, float[] block, double[] divisors, int previousDc, (ushort Code, byte Length)[] dc, (ushort Code, byte Length)[] ac)
    {
        for (int i = 0; i < 8; i++)
        {
            Dct(block, i * 8, 1);
        }

        for (int i = 0; i < 8; i++)
        {
            Dct(block, i, 8);
        }

        Span<int> values = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            int natural = ZigZag[i];
            values[i] = (int)Math.Round(block[natural] / divisors[natural]);
        }

        int difference = values[0] - previousDc;
        int size = BitLength(difference);
        bits.Write(dc[size]);
        WriteValue(bits, difference, size);

        int zeros = 0;
        for (int i = 1; i < 64; i++)
        {
            if (values[i] == 0)
            {
                zeros++;
                continue;
            }

            while (zeros >= 16)
            {
                bits.Write(ac[0xF0]);
                zeros -= 16;
            }

            size = BitLength(values[i]);
            bits.Write(ac[(zeros << 4) | size]);
            WriteValue(bits, values[i], size);
            zeros = 0;
        }

        if (zeros > 0)
        {
            bits.Write(ac[0]); // end of block
        }

        return values[0];
    }

    private static void WriteValue(BitWriter bits, int value, int size)
    {
        if (size > 0)
        {
            bits.Write(((ushort)((value < 0 ? value + (1 << size) - 1 : value) & ((1 << size) - 1)), (byte)size));
        }
    }

    private static int BitLength(int value)
    {
        int magnitude = Math.Abs(value), length = 0;
        while (magnitude > 0)
        {
            magnitude >>= 1;
            length++;
        }

        return length;
    }

    /// <summary>The fast scaled DCT (Arai, Agui and Nakajima) on eight values.</summary>
    private static void Dct(float[] d, int at, int step)
    {
        float d0 = d[at], d1 = d[at + step], d2 = d[at + (2 * step)], d3 = d[at + (3 * step)];
        float d4 = d[at + (4 * step)], d5 = d[at + (5 * step)], d6 = d[at + (6 * step)], d7 = d[at + (7 * step)];
        float t0 = d0 + d7, t7 = d0 - d7, t1 = d1 + d6, t6 = d1 - d6, t2 = d2 + d5, t5 = d2 - d5, t3 = d3 + d4, t4 = d3 - d4;

        float t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        d[at] = t10 + t11;
        d[at + (4 * step)] = t10 - t11;
        float z1 = (t12 + t13) * 0.707106781f;
        d[at + (2 * step)] = t13 + z1;
        d[at + (6 * step)] = t13 - z1;

        t10 = t4 + t5;
        t11 = t5 + t6;
        t12 = t6 + t7;
        float z5 = (t10 - t12) * 0.382683433f;
        float z2 = (t10 * 0.541196100f) + z5;
        float z4 = (t12 * 1.306562965f) + z5;
        float z3 = t11 * 0.707106781f;
        float z11 = t7 + z3, z13 = t7 - z3;
        d[at + (5 * step)] = z13 + z2;
        d[at + (3 * step)] = z13 - z2;
        d[at + step] = z11 + z4;
        d[at + (7 * step)] = z11 - z4;
    }

    private static (ushort Code, byte Length)[] Codes(byte[] bits, byte[] values)
    {
        var codes = new (ushort, byte)[256];
        int code = 0, index = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++)
            {
                codes[values[index++]] = ((ushort)code, (byte)length);
                code++;
            }

            code <<= 1;
        }

        return codes;
    }

    private static void WriteHeaders(Stream o, int width, int height, bool grey, byte[] lumaQ, byte[] chromaQ)
    {
        o.Write([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

        void Quant(int id, byte[] table)
        {
            o.Write([0xFF, 0xDB, 0, 67, (byte)id]);
            for (int i = 0; i < 64; i++)
            {
                o.WriteByte(table[ZigZag[i]]);
            }
        }

        Quant(0, lumaQ);
        if (!grey)
        {
            Quant(1, chromaQ);
        }

        byte h1 = (byte)(height >> 8), h0 = (byte)height, w1 = (byte)(width >> 8), w0 = (byte)width;
        if (grey)
        {
            o.Write([0xFF, 0xC0, 0, 11, 8, h1, h0, w1, w0, 1, 1, 0x11, 0]);
        }
        else
        {
            o.Write([0xFF, 0xC0, 0, 17, 8, h1, h0, w1, w0, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        }

        void Huffman(int cls, byte[] bits, byte[] values)
        {
            int length = 19 + values.Length;
            o.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)cls]);
            o.Write(bits);
            o.Write(values);
        }

        Huffman(0x00, DcLumaBits, DcValues);
        Huffman(0x10, AcLumaBits, AcLumaValues);
        if (!grey)
        {
            Huffman(0x01, DcChromaBits, DcValues);
            Huffman(0x11, AcChromaBits, AcChromaValues);
            o.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        }
        else
        {
            o.Write([0xFF, 0xDA, 0, 8, 1, 1, 0x00, 0, 63, 0]);
        }
    }

    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _count;

        public void Write((ushort Code, byte Length) code)
        {
            _buffer = (_buffer << code.Length) | code.Code;
            _count += code.Length;
            while (_count >= 8)
            {
                byte next = (byte)(_buffer >> (_count - 8));
                output.WriteByte(next);
                if (next == 0xFF)
                {
                    output.WriteByte(0); // a real 0xFF in the data, not a marker
                }

                _count -= 8;
            }

            _buffer &= (1 << _count) - 1;
        }

        /// <summary>Pads the last byte with ones, as the format asks.</summary>
        public void Flush()
        {
            if (_count > 0)
            {
                Write(((ushort)((1 << (8 - _count)) - 1), (byte)(8 - _count)));
            }
        }
    }
}
