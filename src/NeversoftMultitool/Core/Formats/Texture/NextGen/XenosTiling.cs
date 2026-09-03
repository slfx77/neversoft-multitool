namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Xbox 360 (Xenos) texture untiling, in compression-block units.
/// </summary>
/// <remarks>
///     The GPU stores a surface in 32-block-wide macro tiles with a bit-interleaved
///     micro layout; <see cref="GetTiledBlockIndex" /> is the standard
///     XGAddress2DTiledOffset address computation reduced to whole blocks. It is a
///     pure PERMUTATION of blocks, which is also what makes it self-checking: an
///     all-white source can only ever come back all-white, whatever the layout.
///     Validated 2026-08-27 against the PS3 builds of the same games, whose
///     payloads are linear: <b>790 of 794 comparable textures decode
///     pixel-identically</b> (DXT1 629/633, DXT5 161/161). The four residual DXT1
///     mismatches are scattered rather than systematic and are taken to be
///     re-authored art.
/// </remarks>
internal static class XenosTiling
{
    /// <summary>
    ///     Textures smaller than a macro tile on either axis begin 32 BLOCKS into
    ///     their region rather than at its start.
    /// </summary>
    /// <remarks>
    ///     Derived from the data, not assumed: recovering the true linear→stored
    ///     block permutation for sub-32 textures showed it is exactly the ordinary
    ///     tiled permutation shifted by a constant 32 blocks (a 4x4 DXT1 grid maps
    ///     to stored blocks 32,33,36,37, 34,35,38,39, 96,…). Applying the shift
    ///     takes this class from 0/95 to <b>95/95</b> pixel-exact against the PS3
    ///     twins. Before it, every sub-32 texture failed and every larger one
    ///     passed — a split at exactly 32 px, which is what marked it as a layout
    ///     rule rather than re-authored art.
    /// </remarks>
    public const int SubTileBlockOffset = 32;

    /// <summary>True when a surface is smaller than one macro tile on either axis.</summary>
    public static bool IsSubMacroTile(int width, int height)
    {
        return width < 32 || height < 32;
    }

    /// <summary>
    ///     Byte offset of a surface's first block within the region its record
    ///     points at.
    /// </summary>
    public static int GetSurfaceByteOffset(int width, int height, int blockBytes)
    {
        return IsSubMacroTile(width, height) ? SubTileBlockOffset * blockBytes : 0;
    }

    /// <summary>
    ///     Rearranges tiled blocks into linear (row-major) order.
    ///     <paramref name="swapEndian" /> reverses each 16-bit word, which the
    ///     fetch constant's endian field selects.
    /// </summary>
    public static byte[] UntileBlocks(
        ReadOnlySpan<byte> tiled, int width, int height, int blockBytes, bool swapEndian)
    {
        return UntileUnits(tiled, Math.Max(1, (width + 3) / 4), Math.Max(1, (height + 3) / 4),
            blockBytes, swapEndian ? 2 : 0);
    }

    /// <summary>
    ///     Rearranges tiled storage units into linear order. A unit is a
    ///     compression block for DXT, or a single texel for uncompressed formats.
    ///     <paramref name="swapWidth" /> reverses groups of that many bytes (the
    ///     fetch constant's endian field selects 2 for 16-bit and 4 for 32-bit).
    /// </summary>
    public static byte[] UntileUnits(
        ReadOnlySpan<byte> tiled, int unitsX, int unitsY, int unitBytes, int swapWidth)
    {
        return UntileUnits(tiled, unitsX, unitsY, unitBytes, swapWidth, 0, false);
    }

    /// <summary>
    ///     Rearranges a surface whose GPU base begins inside its allocation.
    ///     Small Xenos surfaces use a 32-unit base bias; addressing is circular
    ///     at the allocation boundary rather than spilling into the next record.
    /// </summary>
    public static byte[] UntileUnits(
        ReadOnlySpan<byte> tiled,
        int unitsX,
        int unitsY,
        int unitBytes,
        int swapWidth,
        int sourceBaseOffset,
        bool wrapAtEnd)
    {
        if (unitsX <= 0 || unitsY <= 0 || unitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsX), "Tiled dimensions and unit size must be positive");
        if (swapWidth < 0 || swapWidth > 1 && unitBytes % swapWidth != 0)
            throw new ArgumentOutOfRangeException(nameof(swapWidth), "Endian swap width must divide a storage unit");
        if (sourceBaseOffset < 0 || sourceBaseOffset >= tiled.Length
            || sourceBaseOffset % unitBytes != 0)
            throw new InvalidDataException("Tiled texture base offset is outside its payload allocation");
        if (tiled.Length % unitBytes != 0)
            throw new InvalidDataException(
                "Tiled texture payload is truncated or is not a whole number of storage units");

        var blocksX = unitsX;
        var blocksY = unitsY;
        var blockBytes = unitBytes;
        var log2BlockBytes = System.Numerics.BitOperations.Log2((uint)blockBytes);
        var linearLength = checked(blocksX * blocksY * blockBytes);
        if (tiled.Length < linearLength)
        {
            throw new InvalidDataException(
                $"Tiled texture allocation is truncated (needs at least " +
                $"{linearLength} unique bytes, has {tiled.Length})");
        }

        var linear = new byte[linearLength];
        var usedSourceUnits = new System.Collections.BitArray(tiled.Length / blockBytes);

        for (var by = 0; by < blocksY; by++)
        {
        for (var bx = 0; bx < blocksX; bx++)
        {
            var unbiasedSource = checked(
                GetTiledBlockIndex(bx, by, blocksX, log2BlockBytes) * blockBytes);
            var source = checked(sourceBaseOffset + unbiasedSource);
            if (wrapAtEnd && source >= tiled.Length)
                source %= tiled.Length;
            var destination = checked((by * blocksX + bx) * blockBytes);
            if (source < 0 || source + blockBytes > tiled.Length)
            {
                throw new InvalidDataException(
                    $"Tiled texture payload is truncated at source byte {source} " +
                    $"(needs {source + blockBytes}, has {tiled.Length}; " +
                    $"surface {unitsX}x{unitsY} units of {unitBytes} bytes)");
            }

            var sourceUnit = source / blockBytes;
            if (usedSourceUnits[sourceUnit])
            {
                throw new InvalidDataException(
                    $"Tiled texture allocation aliases storage unit {sourceUnit}; " +
                    "the payload is incomplete for this surface layout");
            }

            usedSourceUnits[sourceUnit] = true;

            var block = tiled.Slice(source, blockBytes);
            if (swapWidth > 1)
            {
                for (var i = 0; i < blockBytes; i += swapWidth)
                for (var j = 0; j < swapWidth; j++)
                    linear[destination + i + j] = block[i + swapWidth - 1 - j];
            }
            else
            {
                block.CopyTo(linear.AsSpan(destination, blockBytes));
            }
        }
        }

        return linear;
    }

    /// <summary>
    ///     Validates that a tiled allocation can address every logical storage
    ///     unit exactly once. This is the allocation-only counterpart to
    ///     <see cref="UntileUnits(ReadOnlySpan{byte},int,int,int,int,int,bool)" />
    ///     used by public format probes before pixel decoding.
    /// </summary>
    public static void ValidateUnitMapping(
        int tiledByteLength,
        int unitsX,
        int unitsY,
        int unitBytes,
        int sourceBaseOffset,
        bool wrapAtEnd)
    {
        if (unitsX <= 0 || unitsY <= 0 || unitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitsX), "Tiled dimensions and unit size must be positive");
        if (tiledByteLength <= 0 || tiledByteLength % unitBytes != 0)
        {
            throw new InvalidDataException(
                "Tiled texture payload is truncated or is not a whole number of storage units");
        }

        if (sourceBaseOffset < 0 || sourceBaseOffset >= tiledByteLength
            || sourceBaseOffset % unitBytes != 0)
        {
            throw new InvalidDataException("Tiled texture base offset is outside its payload allocation");
        }

        var logicalLength = checked(unitsX * unitsY * unitBytes);
        if (tiledByteLength < logicalLength)
        {
            throw new InvalidDataException(
                $"Tiled texture allocation is truncated (needs at least " +
                $"{logicalLength} unique bytes, has {tiledByteLength})");
        }

        var log2UnitBytes = System.Numerics.BitOperations.Log2((uint)unitBytes);
        var usedSourceUnits = new System.Collections.BitArray(tiledByteLength / unitBytes);
        for (var y = 0; y < unitsY; y++)
        {
        for (var x = 0; x < unitsX; x++)
        {
            var source = checked(sourceBaseOffset
                                 + GetTiledBlockIndex(x, y, unitsX, log2UnitBytes) * unitBytes);
            if (wrapAtEnd && source >= tiledByteLength)
                source %= tiledByteLength;
            if (source < 0 || source + unitBytes > tiledByteLength)
            {
                throw new InvalidDataException(
                    $"Tiled texture payload is truncated at source byte {source} " +
                    $"(needs {source + unitBytes}, has {tiledByteLength}; " +
                    $"surface {unitsX}x{unitsY} units of {unitBytes} bytes)");
            }

            var sourceUnit = source / unitBytes;
            if (usedSourceUnits[sourceUnit])
            {
                throw new InvalidDataException(
                    $"Tiled texture allocation aliases storage unit {sourceUnit}; " +
                    "the payload is incomplete for this surface layout");
            }

            usedSourceUnits[sourceUnit] = true;
        }
        }
    }

    /// <summary>
    ///     XGAddress2DTiledOffset in block units: where the GPU keeps the block at
    ///     linear position (<paramref name="blockX" />, <paramref name="blockY" />).
    /// </summary>
    private static int GetTiledBlockIndex(int blockX, int blockY, int blocksX, int log2BlockBytes)
    {
        var alignedWidth = (blocksX + 31) & ~31;

        var macro = ((blockX >> 5) + (blockY >> 5) * (alignedWidth >> 5)) << (log2BlockBytes + 7);
        var micro = ((blockX & 7) + ((blockY & 0xE) << 2)) << log2BlockBytes;
        var offset = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((blockY & 1) << 4);

        var address = ((offset & ~0x1FF) << 3)
                      + ((blockY & 16) << 7)
                      + ((offset & 0x1C0) << 2)
                      + ((((blockY & 8) >> 2) + (blockX >> 3) & 3) << 6)
                      + (offset & 0x3F);

        return address >> log2BlockBytes;
    }
}
