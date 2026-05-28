namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Intra-coefficient DC decoding for Factor 5 M4Decoder. Ports
///     <c>FUN_8029C214</c> (DC size via unary VLC) and <c>FUN_8029CE08</c>
///     (DC value: read size bits, sign-extend if MSB==0).
/// </summary>
internal static class Vid1IntraDc
{
    // Chroma DC size fallback table (DOL 0x8031A604). 8 entries packed as
    // bits_to_consume (>> 17) | value (& 0x1FFFF).
    private static readonly uint[] ChromaDcSizeFallback =
    [
        0x00000000, 0x00060004, 0x00060003, 0x00060000,
        0x00040002, 0x00040002, 0x00040001, 0x00040001
    ];

    /// <summary>
    ///     Decode the number of bits used to encode an A878 block prepass/DC
    ///     coefficient.
    ///     Per the validated Python port of FUN_8029C214, the DOL's
    ///     block-class flag is inverted from the obvious naming:
    ///     "luma blocks" take the 11-bit/table fallback path, while
    ///     chroma/other blocks take the 12-bit unary path.
    /// </summary>
    public static int DecodeSize(Vid1BitReader reader, bool isLuma)
    {
        if (isLuma)
        {
            var peek = reader.PeekBits(11);
            var size = 11;
            for (var iter = 0; iter < 8; iter++)
            {
                if (peek == 1)
                {
                    reader.SkipBits(size);
                    return size + 1;
                }

                peek >>= 1;
                size--;
            }

            var entry = ChromaDcSizeFallback[peek];
            reader.SkipBits((int)(entry >> 17));
            return (int)(entry & 0x1FFFF);
        }
        else
        {
            var peek = reader.PeekBits(12);
            var size = 12;
            for (var iter = 0; iter < 10; iter++)
            {
                if (peek == 1)
                {
                    reader.SkipBits(size);
                    return size;
                }

                peek >>= 1;
                size--;
            }

            // No unary-prefix match: sizes 0..3 encoded as a 2-bit tail.
            var tail = reader.ReadBits(2);
            return 3 - tail;
        }
    }

    /// <summary>
    ///     Read a DC coefficient's magnitude as <paramref name="size" /> bits
    ///     from the stream, sign-extending when the MSB is 0 (standard
    ///     MPEG-4 DC coding).
    /// </summary>
    public static int DecodeValue(Vid1BitReader reader, int size)
    {
        if (size == 0) return 0;
        var value = reader.ReadBits(size);
        if (value >> (size - 1) == 0)
        {
            value = -(value ^ ((1 << size) - 1));
        }

        return value;
    }
}
