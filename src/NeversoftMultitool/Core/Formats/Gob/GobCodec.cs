using System.Buffers.Binary;
using System.IO.Compression;

namespace NeversoftMultitool.Core.Formats.Gob;

/// <summary>
///     Per-chunk compression for the DS GOB container. The codec byte is an ASCII
///     character: <c>'0'</c> stores the chunk verbatim and <c>'z'</c> compresses it.
///
///     The <c>'z'</c> framing looks like zlib and is not: a <c>78 9C</c> header and a
///     deflate body, but the 4-byte big-endian trailer is an Adler-32 **seeded with
///     0 instead of 1**. A stock <see cref="ZLibStream" /> therefore inflates the body
///     correctly and then fails its trailing check — which is why an earlier pass at
///     this format concluded the stored length must be padded or chained. It is not;
///     the length is exact and only the seed differs. Verified on all 11,595 compressed
///     chunks in the three carts.
///
///     The same seed-0 Adler is used for the index's per-chunk checksum array, there
///     over the STORED (still-compressed) bytes — 41,643/41,643 exact — so every read
///     gets an integrity check for free.
/// </summary>
internal static class GobCodec
{
    /// <summary>Chunk is stored verbatim.</summary>
    public const byte Stored = (byte)'0';

    /// <summary>Chunk is `78 9C` + raw deflate + seed-0 Adler-32 trailer.</summary>
    public const byte Zlib = (byte)'z';

    private const int ZlibOverhead = 6; // 2-byte header + 4-byte trailer

    public static bool IsKnownCodec(byte codec)
    {
        return codec is Stored or Zlib;
    }

    /// <summary>
    ///     Decodes one chunk's stored bytes, verifying the index checksum first and,
    ///     for a compressed chunk, the decompressed trailer afterwards.
    ///     <paramref name="maxLength" /> bounds the output (the owning file's declared
    ///     size), so a malformed stream cannot inflate without limit.
    /// </summary>
    public static byte[] Decode(in GobChunk chunk, byte[] stored, long maxLength, string what)
    {
        if (Adler0(stored) != chunk.Checksum)
            throw new InvalidDataException(
                $"{what}: stored checksum mismatch (index says 0x{chunk.Checksum:X8}).");

        if (chunk.Codec == Stored)
        {
            if (stored.LongLength > maxLength)
                throw new InvalidDataException(
                    $"{what}: stored chunk of {stored.Length} bytes exceeds the {maxLength} bytes left in the file.");
            return stored;
        }

        return Inflate(stored, maxLength, what);
    }

    private static byte[] Inflate(byte[] stored, long maxLength, string what)
    {
        if (stored.Length < ZlibOverhead)
            throw new InvalidDataException($"{what}: compressed chunk is only {stored.Length} bytes.");

        // Standard zlib header rules — the container keeps these even though its
        // trailer does not follow the spec.
        var cmf = stored[0];
        var flg = stored[1];
        if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            throw new InvalidDataException(
                $"{what}: bad zlib header {cmf:X2} {flg:X2}.");

        using var input = new MemoryStream(stored, 2, stored.Length - ZlibOverhead, false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxLength)
                throw new InvalidDataException(
                    $"{what}: decompressed output exceeds the {maxLength} bytes left in the file.");
            output.Write(buffer, 0, read);
        }

        var payload = output.ToArray();
        var trailer = BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(stored.Length - 4));
        if (Adler0(payload) != trailer)
            throw new InvalidDataException(
                $"{what}: decompressed checksum mismatch (stream trailer is 0x{trailer:X8}).");
        return payload;
    }

    /// <summary>
    ///     Adler-32 seeded with 0 rather than the standard 1. NMAX is the usual 5552,
    ///     the largest run for which the 32-bit accumulators cannot overflow.
    /// </summary>
    public static uint Adler0(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        const int nmax = 5552;

        uint s1 = 0;
        uint s2 = 0;
        var position = 0;
        while (position < data.Length)
        {
            var run = Math.Min(nmax, data.Length - position);
            foreach (var b in data.Slice(position, run))
            {
                s1 += b;
                s2 += s1;
            }

            s1 %= modulus;
            s2 %= modulus;
            position += run;
        }

        return (s2 << 16) | s1;
    }
}
