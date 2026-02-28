namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Decodes PS1 MDEC video frames from STR bitstream data to RGB24 pixels.
///     Pipeline: VLC bitstream -> dequantize -> inverse zigzag -> IDCT -> YCbCr->RGB.
/// </summary>
public static class MdecDecoder
{
    // ── Frame Decoder ────────────────────────────────────────────────────

    /// <summary>
    ///     Decodes a single MDEC video frame from demuxed bitstream data to RGB24.
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

        var reader = new MdecBitReader(frameData.AsSpan(8));

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

    private static void DecodeBlock(ref MdecBitReader reader, int[] block, int qscale)
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
            block[0] = dc * MdecTables.QuantizationMatrix[0];

        // Read AC coefficients via VLC
        var vectorPos = 0;

        while (!reader.IsExhausted)
        {
            var peek = reader.PeekBits(MdecTables.VlcBits);
            var entry = MdecTables.VlcTable[peek];

            if (entry.BitLength == 0)
            {
                // Invalid/unrecognized code -- skip a bit and try to recover
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

            var matrixPos = MdecTables.ReverseZigZag[vectorPos];

            if (level != 0)
            {
                // Dequantize AC: (level * quantTable[pos] * qscale + 4) >> 3
                block[matrixPos] = (level * MdecTables.QuantizationMatrix[matrixPos] * blockQscale + 4) >> 3;
            }
        }
    }

    // ── IDCT (simple_idct from FFmpeg, LGPL) ─────────────────────────────

    private static void Idct(int[] input, int[] output)
    {
        // Row pass (in-place on input)
        for (var i = 0; i < 8; i++)
            Idct1D(input, i * 8, 1, MdecTables.RowShift, input, i * 8);

        // Column pass (input -> output)
        for (var i = 0; i < 8; i++)
            Idct1D(input, i, 8, MdecTables.ColShift, output, i);
    }

    private static void Idct1D(int[] coeff, int off, int stride, int shift, int[] outBuf, int outOff)
    {
        int i0 = coeff[off],
            i1 = coeff[off + stride],
            i2 = coeff[off + 2 * stride],
            i3 = coeff[off + 3 * stride],
            i4 = coeff[off + 4 * stride],
            i5 = coeff[off + 5 * stride],
            i6 = coeff[off + 6 * stride],
            i7 = coeff[off + 7 * stride];

        var dc = i0 * MdecTables.W4 + (1 << (shift - 1));

        var e0 = dc + i2 * MdecTables.W2;
        var e1 = dc + i2 * MdecTables.W6;
        var e2 = dc - i2 * MdecTables.W6;
        var e3 = dc - i2 * MdecTables.W2;

        var o0 = i1 * MdecTables.W1 + i3 * MdecTables.W3;
        var o1 = i1 * MdecTables.W3 - i3 * MdecTables.W7;
        var o2 = i1 * MdecTables.W5 - i3 * MdecTables.W1;
        var o3 = i1 * MdecTables.W7 - i3 * MdecTables.W5;

        if ((i4 | i5 | i6 | i7) != 0)
        {
            var m44 = i4 * MdecTables.W4;
            e0 += m44 + i6 * MdecTables.W6;
            e1 += -m44 - i6 * MdecTables.W2;
            e2 += -m44 + i6 * MdecTables.W2;
            e3 += m44 - i6 * MdecTables.W6;

            o0 += i5 * MdecTables.W5 + i7 * MdecTables.W7;
            o1 += -i5 * MdecTables.W1 - i7 * MdecTables.W5;
            o2 += i5 * MdecTables.W7 + i7 * MdecTables.W3;
            o3 += i5 * MdecTables.W3 - i7 * MdecTables.W1;
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

    // ── YCbCr -> RGB ──────────────────────────────────────────────────────

    private static byte[] YCbCrToRgb(int[] luma, int[] cr, int[] cb, int width, int height, int chromaStride)
    {
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var lumaVal = luma[y * width + x];

                // Chroma is at half resolution (one Cr/Cb per 2x2 luma pixels)
                var chromaX = x / 2;
                var chromaY = y / 2;
                var chromaIdx = chromaY * chromaStride + chromaX;
                var crVal = cr[chromaIdx];
                var cbVal = cb[chromaIdx];

                // PS1 YCbCr->RGB with fixed-point arithmetic
                var yShift = lumaVal + 128;
                var chromaRed = (int)ShrRound(MdecTables.Cr_R * crVal, 16);
                var chromaGreen = (int)ShrRound(-MdecTables.Cb_G * cbVal - MdecTables.Cr_G * crVal, 16);
                var chromaBlue = (int)ShrRound(MdecTables.Cb_B * cbVal, 16);

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

    private static byte Clamp(int val)
    {
        return (byte)Math.Clamp(val, 0, 255);
    }
}
