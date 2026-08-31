namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
///     Reads the IFD chain of a multi-page TIFF and hands back one
///     single-page TIFF per level.
/// </summary>
/// <remarks>
///     The Proving Ground Wii disc ships 2,099 authoring <c>.tif</c> files
///     (8,531 corpus-wide once THAW GC and THAW PC are counted), and 1,487 of
///     the 2,003 non-empty ones are MIP CHAINS — measured exactly floor-halved
///     at every step, 1,487/1,487, e.g. 256x128 → 128x64 → 64x32 → 32x16 → 16x8.
///     ImageSharp cannot load them: an <c>Image</c> requires uniformly sized
///     frames, so any decode that reaches the second page throws
///     "Images with different sizes are not supported" (verified on 3.1.12 with
///     the default options, with an explicit TiffDecoder, and with
///     <c>MaxFrames = 2</c>).
///     Rather than re-implement TIFF, this rewrites two pointers in a copy of
///     the file: the header's first-IFD pointer is aimed at level N, and level
///     N's next-IFD pointer is zeroed. Every strip/tile offset in a TIFF is
///     absolute, so nothing else has to move and each level decodes through the
///     stock ImageSharp path. Measured over the whole PG Wii tree: 7,308 of
///     7,308 frames decode, 0 failures.
/// </remarks>
public static class TiffMipChain
{
    private const int MaxLevels = 64;

    /// <summary>True when the bytes open with a TIFF header (II*\0 or MM\0*).</summary>
    public static bool IsTiff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;

        var little = data[0] == 0x49 && data[1] == 0x49;
        var big = data[0] == 0x4D && data[1] == 0x4D;
        if (!little && !big) return false;

        return ReadUInt16(data, 2, little) == 42;
    }

    /// <summary>
    ///     File offsets of every IFD in the chain, outermost level first.
    ///     Empty when the bytes are not a walkable TIFF (the corpus also holds
    ///     96 zero-byte <c>.tif</c> files).
    /// </summary>
    public static IReadOnlyList<int> GetLevelOffsets(byte[] data)
    {
        var offsets = new List<int>();
        if (!IsTiff(data)) return offsets;

        var little = data[0] == 0x49;
        var next = (int)ReadUInt32(data, 4, little);
        while (next > 0 && next + 2 <= data.Length && offsets.Count < MaxLevels)
        {
            var entryCount = ReadUInt16(data, next, little);
            var nextPointerPosition = next + 2 + entryCount * 12;
            if (nextPointerPosition + 4 > data.Length) break;

            offsets.Add(next);
            var following = (int)ReadUInt32(data, nextPointerPosition, little);
            if (following <= next) break; // Refuse to loop on a malformed chain.
            next = following;
        }

        return offsets;
    }

    /// <summary>Number of pages, i.e. mip levels including the top.</summary>
    public static int GetLevelCount(byte[] data)
    {
        return GetLevelOffsets(data).Count;
    }

    /// <summary>
    ///     Returns a standalone single-page TIFF for one level, by retargeting
    ///     the header's IFD pointer and cutting that level's chain link.
    /// </summary>
    public static byte[] ExtractLevel(byte[] data, int level)
    {
        var offsets = GetLevelOffsets(data);
        if (level < 0 || level >= offsets.Count)
            throw new ArgumentOutOfRangeException(nameof(level), $"Level {level} of {offsets.Count}");

        if (level == 0 && offsets.Count == 1)
            return data;

        var copy = (byte[])data.Clone();
        var little = copy[0] == 0x49;
        WriteUInt32(copy, 4, (uint)offsets[level], little);

        var entryCount = ReadUInt16(copy, offsets[level], little);
        WriteUInt32(copy, offsets[level] + 2 + entryCount * 12, 0, little);
        return copy;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool little)
    {
        return little
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool little)
    {
        return little
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool little)
    {
        if (little)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
        else
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
