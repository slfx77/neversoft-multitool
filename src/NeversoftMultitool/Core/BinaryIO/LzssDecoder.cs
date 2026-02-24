namespace NeversoftMultitool.Core.BinaryIO;

/// <summary>
/// Decodes LZSS (Lempel-Ziv-Storer-Szymanski) compressed data.
/// Ported from Neversoft's THUG source: Core/compress.cpp and Sys/File/PRE.cpp.
/// Used in PRE v3 archives (THPS3 through THAW, 2001–2005).
/// </summary>
public static class LzssDecoder
{
    private const int RingBufferSize = 4096;
    private const int MatchLimit = 18;
    private const int Threshold = 2;

    /// <summary>
    /// Decompresses LZSS-encoded data.
    /// </summary>
    /// <param name="compressed">The compressed input bytes.</param>
    /// <param name="decompressedSize">Expected output size in bytes.</param>
    /// <returns>Decompressed byte array of exactly <paramref name="decompressedSize"/> bytes.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> compressed, int decompressedSize)
    {
        var output = new byte[decompressedSize];
        var textBuf = new byte[RingBufferSize + MatchLimit - 1];

        // Initialize ring buffer with spaces (matches Neversoft's implementation)
        textBuf.AsSpan(0, RingBufferSize - MatchLimit).Fill(0x20);

        int r = RingBufferSize - MatchLimit;
        int inPos = 0;
        int outPos = 0;
        int len = compressed.Length;
        uint flags = 0;

        while (outPos < decompressedSize)
        {
            if (((flags >>= 1) & 256) == 0)
            {
                if (len <= 0) break;
                len--;
                flags = compressed[inPos++] | 0xFF00u;
            }

            if ((flags & 1) != 0)
            {
                // Literal byte
                if (len <= 0) break;
                len--;
                byte c = compressed[inPos++];
                output[outPos++] = c;
                textBuf[r++] = c;
                r &= RingBufferSize - 1;
            }
            else
            {
                // Back-reference: 12-bit offset + 4-bit length
                if (len <= 0) break;
                len--;
                int i = compressed[inPos++];
                if (len <= 0) break;
                len--;
                int j = compressed[inPos++];

                i |= (j & 0xF0) << 4;       // 12-bit offset
                j = (j & 0x0F) + Threshold;  // 4-bit length + threshold

                for (int k = 0; k <= j; k++)
                {
                    byte c = textBuf[(i + k) & (RingBufferSize - 1)];
                    if (outPos >= decompressedSize) break;
                    output[outPos++] = c;
                    textBuf[r++] = c;
                    r &= RingBufferSize - 1;
                }
            }
        }

        return output;
    }
}
