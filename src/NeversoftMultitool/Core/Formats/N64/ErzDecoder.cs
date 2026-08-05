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
///     ERZ v1 (the THPS1/2/3 asset corpora) is a different scheme: a
///     Deflate-like coder with per-block canonical code tables (three tables
///     of up to 16 codes, built from 5-bit count + 4-bit lengths), an
///     LSB-first 16-bit little-endian bit window, byte-aligned literal runs
///     copied straight from the stream, and a 16-bit match-count prefix per
///     block. Transcribed from the core at RAM 0x80001408 and validated the
///     same way as v2.
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
        return block[3] == 2 ? DecodeV2(block) : DecodeV1(block);
    }


    /// <summary>
    ///     The v1 core (RAM 0x80001408). Mechanical transcription; registers
    ///     keep their MIPS names, comments carry the block addresses.
    /// </summary>
    private static byte[] DecodeV1(byte[] block)
    {
        var decompressedSize = GetDecompressedSize(block);
        if (decompressedSize < 0 || decompressedSize > 1 << 24)
            throw new InvalidDataException($"Implausible ERZ output size {decompressedSize}");

        var output = new byte[decompressedSize];
        var s3 = HeaderSize;     // stream pointer (16-bit LE window chunks + literals)
        var s4 = 0;              // dst
        var s5 = decompressedSize;

        // Three code tables on the "stack": 0x40 bytes of (mask,code) BE16
        // pairs + 0x40 bytes of per-entry metadata each.
        var tables = new byte[3][];
        for (var t = 0; t < 3; t++)
            tables[t] = new byte[0x80];

        byte SrcByte(int index)
        {
            // The v1 window READS AHEAD of the consumed position (the refill
            // fetches two bytes past the current chunk, the literal re-sync
            // peeks the next chunk), so the last few reads of a block land
            // beyond compressedSize. On console those bytes are whatever
            // follows in ROM and their bits are never consumed into output;
            // zero-fill is equally never-consumed and keeps exact slices safe.
            // The golden-hash tests prove no padded byte reaches the output.
            return index >= block.Length ? (byte)0 : block[index];
        }

        // Bit window state (0x1444..0x145C): t6 = LE16 at s3, t7 = bits
        // remaining in the current chunk (starts 0 so the first read refills).
        var t6 = (uint)((SrcByte(s3 + 1) << 8) | SrcByte(s3));
        var t7 = 0;

        // REFILL (0x16C8), parameterized on the pending consume count.
        int Refill(int t1)
        {
            t7 += t1;
            t6 >>= t7;
            s3 += 4;
            t6 |= (uint)(SrcByte(s3 - 2) << 16) | (uint)(SrcByte(s3 - 1) << 24);
            s3 -= 2;
            t1 -= t7;
            t7 = 16 - t1;
            return t1;
        }

        // READBITS (0x1694): value = window & mask BEFORE the shift; then
        // consume count bits (refilling mid-consume when the chunk runs dry).
        uint ReadBits(uint mask, int count)
        {
            var value = t6 & mask;
            t7 -= count;
            if (t7 < 0)
                count = Refill(count);
            t6 >>= count;
            return value;
        }

        // BUILD_TABLE (0x1728 + the canonical builder at 0x17B0).
        void BuildTable(byte[] table)
        {
            var count = (int)ReadBits(0x1F, 5) - 1;                            // 0x1728
            if (count < 0)
                return;

            var lengths = new byte[16];                                       // 0x175C
            for (var index = 0; index <= count; index++)
                lengths[index] = (byte)ReadBits(0xF, 4);

            var t0 = 0x80000000u;                                             // 0x1794
            var t2 = 0u;
            var p = 0;                                                        // running s0
            for (var t1 = 1; t1 != 0x11; t1++)                                // 0x17B0
            {
                for (var t4 = count; t4 >= 0; t4--)                           // 0x17B8
                {
                    var symbolLength = lengths[count - t4];
                    if (symbolLength != t1)                                   // 0x17C0
                        continue;

                    var maskValue = (1 << t1) - 1;                            // 0x17C8
                    table[p + 1] = (byte)maskValue;
                    table[p] = (byte)(maskValue >> 8);
                    p += 2;

                    // Bit-reverse the canonical code accumulator (0x17E4).
                    var t5 = (t2 << 16) | (t2 >> 16);
                    var reversed = 0u;
                    for (var bit = t1 - 1; ; bit--)                           // 0x17FC
                    {
                        reversed &= 0xFFFF;
                        var top = t5 & 0x8000;
                        t5 <<= 1;
                        reversed >>= 1;
                        if (top != 0)
                            reversed |= 0x8000;
                        if (bit == 0)
                            break;
                    }

                    reversed >>= 16 - t1;                                     // 0x1828
                    table[p + 1] = (byte)reversed;
                    table[p] = (byte)(reversed >> 8);
                    p += 2;

                    table[p + 0x3C] = (byte)t1;                               // 0x1844 length
                    var symbol = count - t4;
                    table[p + 0x3D] = (byte)symbol;                           // 0x1850 symbol
                    var extra = (1 << (symbol - 1)) - 1;                      // 0x1854
                    table[p + 0x3F] = (byte)extra;
                    table[p + 0x3E] = (byte)(extra >> 8);

                    t2 += t0;                                                 // 0x1870
                }

                t0 >>= 1;                                                     // 0x187C
            }
        }

        // DECODE_SYM (0x15BC): canonical lookup + gamma-style value read.
        int DecodeSymbol(byte[] table)
        {
            var p = 0;
            while (true)                                                      // 0x15BC
            {
                if (p + 4 > 0x40)
                    throw new InvalidDataException("ERZ v1 code lookup ran off its table");
                var mask = (table[p] << 8) | table[p + 1];
                var code = (table[p + 2] << 8) | table[p + 3];
                p += 4;
                if ((int)(t6 & (uint)mask) - code == 0)
                    break;
            }

            var lengthBits = table[p + 0x3C];                                 // 0x15F4
            t7 -= lengthBits;
            int consume = lengthBits;
            if (t7 < 0)
                consume = Refill(consume);                                    // 0x1610
            t6 >>= consume;                                                   // 0x1620

            int symbol = table[p + 0x3D];                                     // 0x1624
            if (symbol - 2 < 0)                                               // 0x162C
                return symbol;

            var extraBits = symbol - 1;                                       // 0x1638
            var extraMask = (table[p + 0x3E] << 8) | table[p + 0x3F];         // 0x1644
            var value = (int)(t6 & (uint)extraMask);                          // 0x1654
            t7 -= extraBits;                                                  // 0x1658
            consume = extraBits;
            if (t7 < 0)
                consume = Refill(consume);                                    // 0x1664
            t6 >>= consume;                                                   // 0x167C
            return (1 << extraBits) | value;                                  // 0x1680
        }

        ReadBits(2, 2);                                                       // 0x145C header bits

        while (true)
        {
            // 0x1474: per-block table set + 16-bit match count.
            BuildTable(tables[0]);
            BuildTable(tables[1]);
            BuildTable(tables[2]);
            var t4 = ((int)ReadBits(0xFFFFFFFF, 16) - 1) & 0xFFFF;            // 0x14A0

            while (true)
            {
                // 0x1534: literal run.
                var t0 = DecodeSymbol(tables[0]) - 1;                         // 0x1550
                if (t0 >= 0)
                {
                    // 0x18A8: literals are byte-aligned in the stream.
                    while (true)
                    {
                        if (s4 >= output.Length)
                            throw new InvalidDataException("ERZ v1 output overrun");
                        output[s4++] = SrcByte(s3);
                        s3 += 1;
                        if (t0 == 0)
                            break;
                        t0 -= 1;
                    }

                    // 0x1574: re-sync the window over the new stream position,
                    // keeping the t7 not-yet-consumed low bits.
                    var fresh = (uint)((SrcByte(s3 + 1) << 8) | SrcByte(s3)) << t7;
                    var keep = (uint)((1 << t7) - 1);
                    t6 = (t6 & keep) | fresh;
                }

                if (t4 != 0)                                                  // 0x159C
                {
                    t4 -= 1;
                    // 0x14D4: match - distance then length, then a pairwise copy.
                    var distance = DecodeSymbol(tables[1]);
                    var s1 = s4 - distance - 1;                               // 0x14EC
                    if (s1 < 0)
                        throw new InvalidDataException("ERZ v1 match before start of output");
                    var length = DecodeSymbol(tables[2]);                     // 0x14F8

                    if (s4 >= output.Length)
                        throw new InvalidDataException("ERZ v1 output overrun");
                    output[s4++] = output[s1++];                              // 0x150C
                    while (true)                                              // 0x151C
                    {
                        if (s4 >= output.Length)
                            throw new InvalidDataException("ERZ v1 output overrun");
                        output[s4++] = output[s1++];
                        if (length == 0)
                            break;
                        length -= 1;
                    }

                    continue;
                }

                break;                                                        // 0x15A4
            }

            if (s4 >= s5)
                return output;
        }
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
