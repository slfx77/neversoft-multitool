namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The Game Boy Advance BIOS LZ77 codec (<c>LZ77UnCompReadNormalWrite</c>,
///     SWI 0x11) — the compression Vicarious Visions' GBA Tony Hawk engine uses for
///     its packed art. A stream is a <c>0x10</c> type byte, a little-endian u24
///     decompressed size, then flag-byte groups of eight tokens read MSB-first:
///     a 0 bit is one literal byte; a 1 bit is a back-reference of two bytes
///     <c>{ length = (b0 &gt;&gt; 4) + 3, displacement = ((b0 &amp; 0xF) &lt;&lt; 8) | b1 }</c>
///     copying from <c>outPos - displacement - 1</c>.
///
///     <see cref="TryDecompress" /> is <b>strict</b>: it accepts a stream only when
///     every back-reference stays in range and the output lands exactly on the
///     declared size without running past the ROM. That exactness is what makes the
///     codec double as a validator — a plausible 0x10 byte that is not really a
///     stream header fails long before producing a whole image — which is how the
///     ROM's anonymous, table-addressed image assets are located without a
///     directory (the GBA carts carry no filename strings).
/// </summary>
public static class GbaBiosLz77
{
    // Real streams in the corpus sit well inside these bounds; the range only
    // rejects absurd sizes early so a false 0x10 header cannot allocate wildly.
    private const int MinDecompressedSize = 32;
    private const int MaxDecompressedSize = 1 << 20;

    /// <summary>
    ///     Attempts a strict decode of a BIOS LZ77 stream at <paramref name="offset" />.
    ///     Returns false (leaving <paramref name="payload" /> empty) unless the byte
    ///     is a 0x10 header and the stream decodes cleanly to its declared size.
    /// </summary>
    public static bool TryDecompress(
        ReadOnlySpan<byte> data, int offset, out byte[] payload, out int compressedLength)
    {
        payload = [];
        compressedLength = 0;

        if (offset < 0 || offset + 4 > data.Length || data[offset] != 0x10)
            return false;

        var size = data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16);
        if (size is < MinDecompressedSize or > MaxDecompressedSize)
            return false;

        var outBuf = new byte[size];
        var outPos = 0;
        var pos = offset + 4;
        var n = data.Length;

        while (outPos < size)
        {
            if (pos >= n)
                return false;
            var flags = data[pos++];
            for (var bit = 7; bit >= 0 && outPos < size; bit--)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    if (pos + 2 > n)
                        return false;
                    var b0 = data[pos];
                    var b1 = data[pos + 1];
                    pos += 2;
                    var length = (b0 >> 4) + 3;
                    var disp = ((b0 & 0xF) << 8) | b1;
                    var src = outPos - disp - 1;
                    if (src < 0 || outPos + length > size)
                        return false; // out-of-range or overshoot -> not a valid stream here
                    for (var k = 0; k < length; k++)
                        outBuf[outPos++] = outBuf[src++];
                }
                else
                {
                    if (pos >= n)
                        return false;
                    outBuf[outPos++] = data[pos++];
                }
            }
        }

        payload = outBuf;
        compressedLength = pos - offset;
        return true;
    }
}
