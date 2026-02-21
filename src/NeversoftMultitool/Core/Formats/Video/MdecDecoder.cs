namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
/// Decodes PS1 MDEC video frames from STR bitstream data to RGB24 pixels.
/// Pipeline: VLC bitstream → dequantize → inverse zigzag → IDCT → YCbCr→RGB.
/// </summary>
public static class MdecDecoder
{
    // ── Constants ────────────────────────────────────────────────────────

    // PSX default quantization matrix (MPEG-1 standard, from MdecInputStream.java)
    private static readonly int[] QuantizationMatrix =
    [
         2, 16, 19, 22, 26, 27, 29, 34,
        16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48,
        26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83
    ];

    // Reverse zig-zag: maps linear position → 8×8 matrix index
    private static readonly int[] ReverseZigZag =
    [
         0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    // YCbCr→RGB fixed-point coefficients (16-bit fractional, from PsxYCbCr_int.java)
    private const long Cr_R = 91893;   // ~1.402
    private const long Cb_G = 22525;   // ~0.3437
    private const long Cr_G = 46812;   // ~0.7143
    private const long Cb_B = 116224;  // ~1.772

    // IDCT constants (simple_idct from FFmpeg, Loeffler algorithm)
    private const int W1 = 22725, W2 = 21407, W3 = 19266, W4 = 16383;
    private const int W5 = 12873, W6 = 8867, W7 = 4520;
    private const int RowShift = 11, ColShift = 20;

    // ── VLC Lookup Table ─────────────────────────────────────────────────

    private const int VlcBits = 17;
    private const int VlcTableSize = 1 << VlcBits;
    private static readonly VlcEntry[] VlcTable = BuildVlcTable();

    private readonly struct VlcEntry(byte bitLength, byte run, short level, bool isEscape, bool isEndOfBlock)
    {
        public readonly byte BitLength = bitLength;
        public readonly byte Run = run;
        public readonly short Level = level;
        public readonly bool IsEscape = isEscape;
        public readonly bool IsEndOfBlock = isEndOfBlock;
    }

    private static VlcEntry[] BuildVlcTable()
    {
        var table = new VlcEntry[VlcTableSize];

        // MPEG-1 Table B-14 entries: (code_string, run, level)
        // The 's' suffix means a sign bit follows
        ReadOnlySpan<(uint code, int bits, int run, int level)> entries =
        [
            (0b11,               2,  0,  1),
            (0b011,              3,  1,  1),
            (0b0100,             4,  0,  2),
            (0b0101,             4,  2,  1),
            (0b00101,            5,  0,  3),
            (0b00110,            5,  4,  1),
            (0b00111,            5,  3,  1),
            (0b000100,           6,  7,  1),
            (0b000101,           6,  6,  1),
            (0b000110,           6,  1,  2),
            (0b000111,           6,  5,  1),
            (0b0000100,          7,  2,  2),
            (0b0000101,          7,  9,  1),
            (0b0000110,          7,  0,  4),
            (0b0000111,          7,  8,  1),
            (0b00100000,         8, 13,  1),
            (0b00100001,         8,  0,  6),
            (0b00100010,         8, 12,  1),
            (0b00100011,         8, 11,  1),
            (0b00100100,         8,  3,  2),
            (0b00100101,         8,  1,  3),
            (0b00100110,         8,  0,  5),
            (0b00100111,         8, 10,  1),
            (0b0000001000,      10, 16,  1),
            (0b0000001001,      10,  5,  2),
            (0b0000001010,      10,  0,  7),
            (0b0000001011,      10,  2,  3),
            (0b0000001100,      10,  1,  4),
            (0b0000001101,      10, 15,  1),
            (0b0000001110,      10, 14,  1),
            (0b0000001111,      10,  4,  2),
            (0b000000010000,    12,  0, 11),
            (0b000000010001,    12,  8,  2),
            (0b000000010010,    12,  4,  3),
            (0b000000010011,    12,  0, 10),
            (0b000000010100,    12,  2,  4),
            (0b000000010101,    12,  7,  2),
            (0b000000010110,    12, 21,  1),
            (0b000000010111,    12, 20,  1),
            (0b000000011000,    12,  0,  9),
            (0b000000011001,    12, 19,  1),
            (0b000000011010,    12, 18,  1),
            (0b000000011011,    12,  1,  5),
            (0b000000011100,    12,  3,  3),
            (0b000000011101,    12,  0,  8),
            (0b000000011110,    12,  6,  2),
            (0b000000011111,    12, 17,  1),
            (0b0000000010000,   13, 10,  2),
            (0b0000000010001,   13,  9,  2),
            (0b0000000010010,   13,  5,  3),
            (0b0000000010011,   13,  3,  4),
            (0b0000000010100,   13,  2,  5),
            (0b0000000010101,   13,  1,  7),
            (0b0000000010110,   13,  1,  6),
            (0b0000000010111,   13,  0, 15),
            (0b0000000011000,   13,  0, 14),
            (0b0000000011001,   13,  0, 13),
            (0b0000000011010,   13,  0, 12),
            (0b0000000011011,   13, 26,  1),
            (0b0000000011100,   13, 25,  1),
            (0b0000000011101,   13, 24,  1),
            (0b0000000011110,   13, 23,  1),
            (0b0000000011111,   13, 22,  1),
            (0b00000000010000,  14,  0, 31),
            (0b00000000010001,  14,  0, 30),
            (0b00000000010010,  14,  0, 29),
            (0b00000000010011,  14,  0, 28),
            (0b00000000010100,  14,  0, 27),
            (0b00000000010101,  14,  0, 26),
            (0b00000000010110,  14,  0, 25),
            (0b00000000010111,  14,  0, 24),
            (0b00000000011000,  14,  0, 23),
            (0b00000000011001,  14,  0, 22),
            (0b00000000011010,  14,  0, 21),
            (0b00000000011011,  14,  0, 20),
            (0b00000000011100,  14,  0, 19),
            (0b00000000011101,  14,  0, 18),
            (0b00000000011110,  14,  0, 17),
            (0b00000000011111,  14,  0, 16),
            (0b000000000010000, 15,  0, 40),
            (0b000000000010001, 15,  0, 39),
            (0b000000000010010, 15,  0, 38),
            (0b000000000010011, 15,  0, 37),
            (0b000000000010100, 15,  0, 36),
            (0b000000000010101, 15,  0, 35),
            (0b000000000010110, 15,  0, 34),
            (0b000000000010111, 15,  0, 33),
            (0b000000000011000, 15,  0, 32),
            (0b000000000011001, 15,  1, 14),
            (0b000000000011010, 15,  1, 13),
            (0b000000000011011, 15,  1, 12),
            (0b000000000011100, 15,  1, 11),
            (0b000000000011101, 15,  1, 10),
            (0b000000000011110, 15,  1,  9),
            (0b000000000011111, 15,  1,  8),
            (0b0000000000010000, 16, 1, 18),
            (0b0000000000010001, 16, 1, 17),
            (0b0000000000010010, 16, 1, 16),
            (0b0000000000010011, 16, 1, 15),
            (0b0000000000010100, 16, 6,  3),
            (0b0000000000010101, 16, 16, 2),
            (0b0000000000010110, 16, 15, 2),
            (0b0000000000010111, 16, 14, 2),
            (0b0000000000011000, 16, 13, 2),
            (0b0000000000011001, 16, 12, 2),
            (0b0000000000011010, 16, 11, 2),
            (0b0000000000011011, 16, 31, 1),
            (0b0000000000011100, 16, 30, 1),
            (0b0000000000011101, 16, 29, 1),
            (0b0000000000011110, 16, 28, 1),
            (0b0000000000011111, 16, 27, 1),
        ];

        // Fill run/level entries (each has a +1 sign bit)
        foreach (var (code, bits, run, level) in entries)
        {
            var totalBits = bits + 1; // +1 for sign bit
            var shift = VlcBits - totalBits;
            if (shift < 0) continue;

            var baseIndex = (int)(code << (shift + 1));
            var count = 1 << shift;

            // Sign = 0 → positive level
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry((byte)totalBits, (byte)run, (short)level, false, false);

            // Sign = 1 → negative level
            baseIndex |= count; // set the sign bit position
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry((byte)totalBits, (byte)run, (short)(-level), false, false);
        }

        // End of Block: "10" (2 bits)
        {
            var shift = VlcBits - 2;
            var baseIndex = 0b10 << shift;
            var count = 1 << shift;
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry(2, 0, 0, false, true);
        }

        // Escape code: "000001" (6 bits) — followed by raw 6-bit run + 10-bit level
        {
            var shift = VlcBits - 6;
            var baseIndex = 0b000001 << shift;
            var count = 1 << shift;
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry(6, 0, 0, true, false);
        }

        return table;
    }

    // ── Bit Reader ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads bits MSB-first from data stored in 16-bit little-endian word order.
    /// STR v2 convention: bytes are read as 16-bit LE words, bits within each word are MSB-first.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitPos;
        private readonly int _totalBits;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitPos = 0;
            _totalBits = data.Length * 8;
        }

        public bool IsExhausted => _bitPos >= _totalBits;

        /// <summary>
        /// Reads a 16-bit word at the given byte offset in STR v2 byte order (byte-swapped LE).
        /// Returns the word with MSB-first bit ordering for extraction.
        /// </summary>
        private ushort ReadWord(int byteOffset)
        {
            // STR v2: bytes within each 16-bit word are swapped (XOR 1)
            // Word at byte offset N: high byte = data[N^1], low byte = data[(N+1)^1]
            // For aligned word at offset 0: high = data[1], low = data[0] → big-endian read of LE data
            var alignedBase = byteOffset & ~1;
            if (alignedBase + 1 < _data.Length)
                return (ushort)((_data[alignedBase + 1] << 8) | _data[alignedBase]);
            if (alignedBase < _data.Length)
                return (ushort)(_data[alignedBase] & 0xFF);
            return 0;
        }

        public uint PeekBits(int count)
        {
            if (_bitPos >= _totalBits) return 0;

            // Build a 32-bit buffer from the current position
            var bytePos = _bitPos >> 3;
            var bitOffset = _bitPos & 15; // bit offset within current 16-bit word
            var wordBase = bytePos & ~1; // align to 16-bit word boundary

            // Read up to 3 consecutive words to get enough bits
            uint buf = ReadWord(wordBase);
            buf = (buf << 16) | ReadWord(wordBase + 2);

            // We have 32 bits starting from wordBase. Shift to align with our bit position.
            // Our bit is at offset bitOffset within the first word.
            buf <<= bitOffset;

            // If we need more than (32 - bitOffset) bits, read another word
            if (bitOffset + count > 32)
            {
                uint extra = ReadWord(wordBase + 4);
                buf |= extra >> (16 - bitOffset);
            }

            return buf >> (32 - count);
        }

        public uint ReadBits(int count)
        {
            var result = PeekBits(count);
            _bitPos += count;
            return result;
        }

        public int ReadSignedBits(int count)
        {
            var val = (int)ReadBits(count);
            // Sign-extend
            var signBit = 1 << (count - 1);
            if ((val & signBit) != 0)
                val |= ~((1 << count) - 1);
            return val;
        }

        public void SkipBits(int count) => _bitPos += count;
    }

    // ── Frame Decoder ────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a single MDEC video frame from demuxed bitstream data to RGB24.
    /// </summary>
    /// <param name="frameData">Assembled bitstream data (all video chunks concatenated)</param>
    /// <param name="width">Frame width in pixels</param>
    /// <param name="height">Frame height in pixels</param>
    /// <returns>RGB24 pixel data (width * height * 3 bytes)</returns>
    public static byte[] DecodeFrame(byte[] frameData, int width, int height)
    {
        // Parse 8-byte bitstream header
        // u16: halfMdecCodeCountCeil32
        // u16: magic 0x3800
        // u16: quantization scale
        // u16: version
        var qscale = BitConverter.ToUInt16(frameData, 4);

        var reader = new BitReader(frameData.AsSpan(8));

        var mbWidth = (width + 15) / 16;
        var mbHeight = (height + 15) / 16;

        // Allocate buffers
        var crBuffer = new int[mbWidth * 8 * mbHeight * 8];
        var cbBuffer = new int[crBuffer.Length];
        var lumaBuffer = new int[width * height];

        var block = new int[64];
        var idctOut = new int[64];

        for (var mbY = 0; mbY < mbHeight; mbY++)
        {
            for (var mbX = 0; mbX < mbWidth; mbX++)
            {
                // Each macroblock: Cr, Cb, Y0, Y1, Y2, Y3
                for (var blockIdx = 0; blockIdx < 6; blockIdx++)
                {
                    DecodeBlock(ref reader, block, qscale);
                    Idct(block, idctOut);

                    switch (blockIdx)
                    {
                        case 0: // Cr
                            WriteChromaBlock(idctOut, crBuffer, mbX, mbY, mbWidth * 8);
                            break;
                        case 1: // Cb
                            WriteChromaBlock(idctOut, cbBuffer, mbX, mbY, mbWidth * 8);
                            break;
                        case 2: // Y0 (top-left)
                            WriteLumaBlock(idctOut, lumaBuffer, mbX * 16, mbY * 16, width);
                            break;
                        case 3: // Y1 (top-right)
                            WriteLumaBlock(idctOut, lumaBuffer, mbX * 16 + 8, mbY * 16, width);
                            break;
                        case 4: // Y2 (bottom-left)
                            WriteLumaBlock(idctOut, lumaBuffer, mbX * 16, mbY * 16 + 8, width);
                            break;
                        case 5: // Y3 (bottom-right)
                            WriteLumaBlock(idctOut, lumaBuffer, mbX * 16 + 8, mbY * 16 + 8, width);
                            break;
                    }
                }
            }
        }

        // Convert YCbCr to RGB24
        return YCbCrToRgb(lumaBuffer, crBuffer, cbBuffer, width, height, mbWidth * 8);
    }

    private static void DecodeBlock(ref BitReader reader, int[] block, int qscale)
    {
        Array.Clear(block);

        if (reader.IsExhausted) return;

        // First code: top 6 bits = quantization scale (overrides frame qscale), bottom 10 bits = DC coefficient
        var firstCode = (int)reader.ReadBits(16);
        var blockQscale = (firstCode >> 10) & 0x3F;
        var dc = firstCode & 0x3FF;

        // Sign-extend DC (10-bit signed)
        if (dc >= 512) dc -= 1024;

        if (blockQscale == 0) blockQscale = qscale;

        // DC: multiply by quantization table[0] only (no qscale factor)
        if (dc != 0)
            block[0] = dc * QuantizationMatrix[0];

        // Read AC coefficients via VLC
        var vectorPos = 0;

        while (!reader.IsExhausted)
        {
            var peek = reader.PeekBits(VlcBits);
            var entry = VlcTable[peek];

            if (entry.BitLength == 0)
            {
                // Invalid/unrecognized code — skip a bit and try to recover
                reader.SkipBits(1);
                continue;
            }

            reader.SkipBits(entry.BitLength);

            if (entry.IsEndOfBlock)
                break;

            int run, level;

            if (entry.IsEscape)
            {
                // Escape: read 6-bit run + 10-bit signed level
                run = (int)reader.ReadBits(6);
                level = reader.ReadSignedBits(10);
            }
            else
            {
                run = entry.Run;
                level = entry.Level;
            }

            vectorPos += run + 1;
            if (vectorPos >= 64) break;

            var matrixPos = ReverseZigZag[vectorPos];

            if (level != 0)
            {
                // Dequantize AC: (level * quantTable[pos] * qscale + 4) >> 3
                block[matrixPos] = (level * QuantizationMatrix[matrixPos] * blockQscale + 4) >> 3;
            }
        }
    }

    // ── IDCT (simple_idct from FFmpeg, LGPL) ─────────────────────────────

    private static void Idct(int[] input, int[] output)
    {
        // Row pass (in-place on input)
        for (var i = 0; i < 8; i++)
            Idct1D(input, i * 8, 1, RowShift, input, i * 8);

        // Column pass (input → output)
        for (var i = 0; i < 8; i++)
            Idct1D(input, i, 8, ColShift, output, i);
    }

    private static void Idct1D(int[] coeff, int off, int stride, int shift, int[] outBuf, int outOff)
    {
        int i0 = coeff[off], i1 = coeff[off + stride], i2 = coeff[off + 2 * stride],
            i3 = coeff[off + 3 * stride], i4 = coeff[off + 4 * stride], i5 = coeff[off + 5 * stride],
            i6 = coeff[off + 6 * stride], i7 = coeff[off + 7 * stride];

        var dc = i0 * W4 + (1 << (shift - 1));

        int e0 = dc + i2 * W2;
        int e1 = dc + i2 * W6;
        int e2 = dc - i2 * W6;
        int e3 = dc - i2 * W2;

        int o0 = i1 * W1 + i3 * W3;
        int o1 = i1 * W3 - i3 * W7;
        int o2 = i1 * W5 - i3 * W1;
        int o3 = i1 * W7 - i3 * W5;

        if ((i4 | i5 | i6 | i7) != 0)
        {
            int m44 = i4 * W4;
            e0 += m44 + i6 * W6;
            e1 += -m44 - i6 * W2;
            e2 += -m44 + i6 * W2;
            e3 += m44 - i6 * W6;

            o0 += i5 * W5 + i7 * W7;
            o1 += -i5 * W1 - i7 * W5;
            o2 += i5 * W7 + i7 * W3;
            o3 += i5 * W3 - i7 * W1;
        }

        outBuf[outOff] = (e0 + o0) >> shift;
        outBuf[outOff + 7 * stride] = (e0 - o0) >> shift;
        outBuf[outOff + stride] = (e1 + o1) >> shift;
        outBuf[outOff + 6 * stride] = (e1 - o1) >> shift;
        outBuf[outOff + 2 * stride] = (e2 + o2) >> shift;
        outBuf[outOff + 5 * stride] = (e2 - o2) >> shift;
        outBuf[outOff + 3 * stride] = (e3 + o3) >> shift;
        outBuf[outOff + 4 * stride] = (e3 - o3) >> shift;
    }

    // ── Buffer writing ───────────────────────────────────────────────────

    private static void WriteChromaBlock(int[] block, int[] buffer, int mbX, int mbY, int stride)
    {
        var baseX = mbX * 8;
        var baseY = mbY * 8;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                buffer[(baseY + y) * stride + baseX + x] = block[y * 8 + x];
            }
        }
    }

    private static void WriteLumaBlock(int[] block, int[] buffer, int baseX, int baseY, int stride)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var px = baseX + x;
                var py = baseY + y;
                if (px < stride && py < buffer.Length / stride)
                    buffer[py * stride + px] = block[y * 8 + x];
            }
        }
    }

    // ── YCbCr → RGB ──────────────────────────────────────────────────────

    private static byte[] YCbCrToRgb(int[] luma, int[] cr, int[] cb, int width, int height, int chromaStride)
    {
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var lumaVal = luma[y * width + x];

                // Chroma is at half resolution (one Cr/Cb per 2×2 luma pixels)
                var chromaX = x / 2;
                var chromaY = y / 2;
                var chromaIdx = chromaY * chromaStride + chromaX;
                var crVal = cr[chromaIdx];
                var cbVal = cb[chromaIdx];

                // PS1 YCbCr→RGB with fixed-point arithmetic
                var yShift = lumaVal + 128;
                var chromaRed = (int)ShrRound(Cr_R * crVal, 16);
                var chromaGreen = (int)ShrRound(-Cb_G * cbVal - Cr_G * crVal, 16);
                var chromaBlue = (int)ShrRound(Cb_B * cbVal, 16);

                var rgbIdx = (y * width + x) * 3;
                rgb[rgbIdx] = Clamp(yShift + chromaRed);
                rgb[rgbIdx + 1] = Clamp(yShift + chromaGreen);
                rgb[rgbIdx + 2] = Clamp(yShift + chromaBlue);
            }
        }

        return rgb;
    }

    private static long ShrRound(long val, int shift)
    {
        if (shift == 0 || val == 0) return val;
        var carry = (val >> (shift - 1)) & 1;
        return (val >> shift) + carry;
    }

    private static byte Clamp(int val) => (byte)Math.Clamp(val, 0, 255);
}
