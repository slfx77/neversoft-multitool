using System.Numerics;
using static NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal static class ThawZoneTexHeaderLayoutSupport
{
    internal static byte[] TransformPsmt4LinearBlocks(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        int blockWidthPixels,
        int blockHeightPixels,
        IReadOnlyList<int> bitPermutation,
        int xorMask)
    {
        if (texData.IsEmpty)
            return [];

        if (width % blockWidthPixels != 0 || height % blockHeightPixels != 0)
            return texData.ToArray();

        var blocksPerRow = width / blockWidthPixels;
        var blockRows = height / blockHeightPixels;
        var blockCount = blocksPerRow * blockRows;
        var expectedBitCount = BitOperations.TrailingZeroCount((uint)blockCount);
        if (!BitOperations.IsPow2((uint)blockCount) || bitPermutation.Count != expectedBitCount)
            return texData.ToArray();

        var rowBytes = width / 2;
        var blockRowBytes = blockWidthPixels / 2;
        var transformed = new byte[texData.Length];

        for (var sourceIndex = 0; sourceIndex < blockCount; sourceIndex++)
        {
            var destIndex = PermuteBits(sourceIndex, bitPermutation) ^ xorMask;
            var sourceX = sourceIndex % blocksPerRow * blockRowBytes;
            var sourceY = sourceIndex / blocksPerRow * blockHeightPixels;
            var destX = destIndex % blocksPerRow * blockRowBytes;
            var destY = destIndex / blocksPerRow * blockHeightPixels;

            for (var row = 0; row < blockHeightPixels; row++)
                texData.Slice((sourceY + row) * rowBytes + sourceX, blockRowBytes)
                    .CopyTo(transformed.AsSpan((destY + row) * rowBytes + destX, blockRowBytes));
        }

        return transformed;
    }

    internal static byte[] TransformPsmt4SlotBlocks(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        IReadOnlyList<int> bitPermutation,
        int xorMask)
    {
        return TransformPsmt4LinearBlocks(texData, width, height, 32, 16, bitPermutation, xorMask);
    }

    internal static byte[] TransformPsmt4SlotBlocksForLayout(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        uint layoutMode)
    {
        return layoutMode switch
        {
            0x02000001 => TransformPsmt4SlotBlocks(texData, width, height, Layout02000001BlockPermutation, 0x07),
            0x02000005 => TransformPsmt4SlotBlocks(texData, width, height, Layout02000005BlockPermutation, 0x16),
            _ => texData.ToArray()
        };
    }

    internal static bool ShouldApplyPsmt4SlotLayoutTransform(uint layoutMode, int selectedBias)
    {
        return layoutMode is 0x02000001 or 0x02000005;
    }

    internal static bool ShouldApplyPsmt4SlotTileTransform(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        if (selectedBias <= 0 || entry.LayoutMode != 0x02000005)
            return false;
        if (entry.DataSize != 0x2000 || entry.PaletteBytes != 0x40 || !matchedUpload.HasValue)
            return false;

        var upload = matchedUpload.Value;
        return upload.Width == 64 && upload.Height == 32;
    }

    internal static bool ShouldPreferPsmt4BiasedAutoSlotCandidate(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        if (selectedBias <= 0 || entry.LayoutMode != 0x02000001)
            return false;
        if (entry.DataSize != 0x2000 || entry.PaletteBytes != 0x20 || entry.DataOffset < 0x138B20 ||
            !matchedUpload.HasValue)
            return false;

        var width = 1 << (int)((entry.Tex0 >> 26) & 0xF);
        var height = 1 << (int)((entry.Tex0 >> 30) & 0xF);
        if (width != 128 || height != 128)
            return false;

        var upload = matchedUpload.Value;
        return upload.Width == 64 && upload.Height == 32;
    }

    internal static bool ShouldPreferNobiasForBias32Bucket(
        ZoneTexHeaderEntry entry,
        int selectedBias,
        VramUpload? matchedUpload)
    {
        if (selectedBias <= 0 || entry.LayoutMode != 0x02000001)
            return false;
        if (entry.DataSize != 0x2000 || entry.PaletteBytes != 0x20 || entry.DataOffset < 0x100000 ||
            !matchedUpload.HasValue)
            return false;

        var width = 1 << (int)((entry.Tex0 >> 26) & 0xF);
        var height = 1 << (int)((entry.Tex0 >> 30) & 0xF);
        if (width != 128 || height != 128)
            return false;

        var upload = matchedUpload.Value;
        return upload.Width == 64 && upload.Height == 32;
    }

    internal static byte[] ReorderClut(byte[] clut, int[] table, int entrySize)
    {
        if (clut.Length < table.Length * entrySize)
            return clut;

        var reordered = new byte[table.Length * entrySize];
        for (var i = 0; i < table.Length; i++)
            Buffer.BlockCopy(clut, table[i] * entrySize, reordered, i * entrySize, entrySize);
        return reordered;
    }

    private static int PermuteBits(int value, IReadOnlyList<int> bitPermutation)
    {
        var result = 0;
        for (var bit = 0; bit < bitPermutation.Count; bit++)
            result |= ((value >> bitPermutation[bit]) & 1) << bit;
        return result;
    }
}
