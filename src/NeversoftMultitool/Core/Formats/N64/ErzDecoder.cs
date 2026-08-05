using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.N64;

/// <summary>
///     Decoder for Edge of Reality's "ERZ" compression (THPS1/2/3 + Spider-Man
///     N64). No public RE of this format exists; this is a mechanical
///     transcription of the version-2 decompressor in the ROMs' boot segment
///     (THPS2 core at RAM 0x80000CF8, located by its <c>lui 0x4552</c>
///     magic-check dispatch), validated byte-for-byte against
///     <c>tools/diagnostics/erz_emu_decode.py</c>, which executes the original
///     MIPS code under emulation.
///
///     Block layout (big-endian): <c>"ERZ"</c> + version byte, u16 0x0001,
///     u16 0, u32 decompressedSize, u32 compressedSize, 6 opaque bytes the
///     decoder never reads, then the bitstream at +18. The scheme is an LZ
///     with an MSB-first bit buffer carrying a low-bit sentinel (a byte
///     refills when the marker exits through bit 8), byte literals interleaved
///     in the same stream, 4-byte-aligned uncompressed runs, an RLE fill for
///     distance-0 matches, and a 16-bit byte-swapped extended-distance form.
///     Labels in the body are the ROM addresses of the corresponding basic
///     blocks so the transcription can be re-checked against the disassembly.
///
///     ERZ v1 (THPS1's ROM) uses a separate core (RAM 0x80001340) and is not
///     transcribed yet; <see cref="Decode" /> rejects it explicitly.
/// </summary>
public static class ErzDecoder
{
    public const int HeaderSize = 18;

    public static bool IsErz(ReadOnlySpan<byte> data)
    {
        return data.Length >= HeaderSize
               && data[0] == (byte)'E' && data[1] == (byte)'R' && data[2] == (byte)'Z'
               && data[3] is 1 or 2;
    }

    public static int GetVersion(ReadOnlySpan<byte> block)
    {
        return block[3];
    }

    public static int GetDecompressedSize(ReadOnlySpan<byte> block)
    {
        return BinaryPrimitives.ReadInt32BigEndian(block[4..]);
    }

    public static int GetCompressedSize(ReadOnlySpan<byte> block)
    {
        return BinaryPrimitives.ReadInt32BigEndian(block[8..]);
    }

    public static byte[] Decode(byte[] block)
    {
        if (!IsErz(block))
            throw new InvalidDataException("Not an ERZ block");
        if (block[3] != 2)
        {
            throw new NotSupportedException(
                $"ERZ v{block[3]} is not implemented (only the v2 core is transcribed)");
        }

        return DecodeV2(block);
    }

    /// <summary>
    ///     The v2 core. Registers keep their MIPS names in comments; control
    ///     flow mirrors the ROM's basic blocks (labels = RAM addresses).
    /// </summary>
    private static byte[] DecodeV2(byte[] block)
    {
        var decompressedSize = GetDecompressedSize(block);
        if (decompressedSize < 0 || decompressedSize > 1 << 24)
            throw new InvalidDataException($"Implausible ERZ output size {decompressedSize}");

        var output = new byte[decompressedSize];
        var i = HeaderSize;      // v1 — stream pointer
        var o = 0;               // a1 — output pointer
        int t0 = 0, t1 = 0, t4;
        int t6;                  // match source (output index)

        byte NextByte()
        {
            if (i >= block.Length)
                throw new InvalidDataException("ERZ stream overrun");
            return block[i++];
        }

        void Put(byte value)
        {
            if (o >= output.Length)
                throw new InvalidDataException("ERZ output overrun");
            output[o++] = value;
        }

        // INIT (0x80000CF8..0x80000DB8): load first stream byte, seed the
        // sentinel, pre-extract one bit.
        var t2 = NextByte() * 2 + 1;   // (B << 1) | marker
        t2 += t2;
        var t7 = (t2 >> 8) & 1;

        L1110: // main dispatch: 0 = literal byte, 1 = match/control
        t2 += t2;
        t7 = (t2 >> 8) & 1;
        if (t7 != 0)
            goto L1148;
        Put(NextByte()); // 0x1124 literal
        t2 += t2;
        t7 = (t2 >> 8) & 1;
        if (t7 == 0)
        {
            // 0x1100 second literal slot in the unrolled pair
            Put(NextByte());
            goto L1110;
        }

        L1148:
        t2 &= 0xFF;
        if (t2 == 0)
        {
            // 0x10E0: the extracted bit was the sentinel — refill and
            // re-dispatch on the true bit.
            t2 = NextByte() * 2 + t7;
            t7 = (t2 >> 8) & 1;
            if (t7 == 0)
            {
                Put(NextByte());
                goto L1110;
            }
        }

        // 0x1154: begin match decode
        t0 = 2;
        t1 = 0;
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0x115C
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x1230
        if (t7 == 0)                                                          // 0x1174
            goto LF28;

        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0x117C
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x124C
        if (t7 == 0)                                                          // 0x1194
            goto L1044;

        t0 += 1;                                                              // 0x119C
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0x11A0
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x1268
        if (t7 == 0)                                                          // 0x11B8
            goto LFA0;

        // 0x11C0: explicit length byte; 0 is the end-of-stream escape
        t0 = NextByte();
        if (t0 == 0)
            goto L12BC;
        t0 += 8;
        goto LFA0;

        LF28: // gamma-style length accumulation into t0
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xDF0
        t0 = t0 + t0 + t7;                                                    // 0xF40
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0xF48
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE0C
        if (t7 == 0)                                                          // 0xF60
            goto LFA0;
        t0 -= 1;                                                              // 0xF68
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0xF6C
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE28
        t0 = t0 + t0 + t7;                                                    // 0xF84
        t7 = (t0 >> 16) & 1;
        if (t0 == 9)
            goto LEB4;

        LFA0: // distance decode
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE44
        if (t7 == 0)                                                          // 0xFB8
            goto L1044;
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0xFC0
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE60
        t1 = t1 + t1 + t7;                                                    // 0xFD8
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0xFE0
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE7C
        if (t7 != 0)                                                          // 0xFF8
            goto L11DC;
        if (t1 != 0)                                                          // 0x1000
            goto L1034;
        t1 += 1;                                                              // 0x1008

        L100C:
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0xE98
        t1 = t1 + t1 + t7;                                                    // 0x1024
        t7 = (t1 >> 16) & 1;

        L1034: // byte-swap the low 16 bits of the accumulated distance high part
        t4 = (t1 << 8) & 0xFF00;
        t1 = (int)((uint)t1 >> 8) | t4;

        L1044: // low distance byte from the stream; copy the match
        var s3 = t1 & 0xFF00;
        t1 = NextByte() | s3;
        t6 = o - t1 - 1;
        if (t6 < 0)
            throw new InvalidDataException("ERZ match before start of output");
        t7 = t0 & 1;                                                          // 0x1060
        t0 = (int)((uint)t0 >> 1);
        if (t7 != 0)
        {
            Put(output[t6]);                                                  // 0x1070
            t6 += 1;
        }

        t0 -= 1;                                                              // 0x1080
        if (t1 != 0)
            goto L10B8;

        // 0x108C: distance 0 — RLE fill with the byte at the match source
        var fill = output[t6];
        do
        {
            Put(fill);                                                        // 0x109C
            t0 -= 1;
            Put(fill);
        } while (t0 >= 0);

        goto L1110;

        L10B8: // pairwise match copy (overlap-safe: byte loads/stores)
        do
        {
            var b0 = output[t6];
            var b1 = output[t6 + 1];
            Put(b0);
            Put(b1);
            t6 += 2;
            t0 -= 1;
        } while (t0 >= 0);

        goto L1110;

        L11DC: // extended distance: one more accumulated bit, then |= 4
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x1284
        t1 = t1 + t1 + t7;                                                    // 0x11F4
        t7 = (t1 >> 16) & 1;
        t1 |= 4;
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                             // 0x1208
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x12A0
        if (t7 != 0)                                                          // 0x1220
            goto L1034;
        goto L100C;

        L12BC: // end-of-stream escape: one bit decides continue vs exit
        t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;
        if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }        // 0x12D4
        if (t7 != 0)
            goto L1110;
        // 0x12F4: the scratch drain reads the u16 the init zeroed — a no-op —
        // then the core returns success.
        if (o != output.Length)
        {
            throw new InvalidDataException(
                $"ERZ stream ended at {o} of {output.Length} output bytes");
        }

        return output;

        LEB4: // length == 9 escape: 4 more bits, then a 4-byte-aligned raw run
        t0 = 3;
        do
        {
            t2 += t2; t7 = (t2 >> 8) & 1; t2 &= 0xFF;                         // 0xEB8
            if (t2 == 0) { t2 = NextByte() * 2 + t7; t7 = (t2 >> 8) & 1; }    // 0xDD4
            t1 = t1 + t1 + t7;                                                // 0xED0
            t7 = (t1 >> 16) & 1;
            t0 -= 1;
        } while (t0 >= 0);

        t1 += 2;                                                              // 0xEEC
        do
        {
            Put(NextByte());                                                  // 0xEF0
            Put(NextByte());
            Put(NextByte());
            Put(NextByte());
            t1 -= 1;
        } while (t1 >= 0);

        goto L1110;
    }
}
