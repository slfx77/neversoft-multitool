using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

/// <summary>
///     Simulates PS2 GS VRAM allocation for texture dictionaries.
///     Replicates the algorithm from THUG texture.cpp LoadTextureGroup() to compute
///     TBP (Texture Base Pointer) addresses for each texture. Used to map GEOM DMA
///     chain TEX0_1 register values back to texture checksums in THPS4 where
///     CGeomNode.texture_checksum is always 0.
/// </summary>
public static class Ps2VramAllocator
{
    // PS2 GS Pixel Storage Modes
    private const uint PSMCT32 = 0x00;
    private const uint PSMCT24 = 0x01;
    private const uint PSMCT16 = 0x02;
    private const uint PSMT8 = 0x13;
    private const uint PSMT4 = 0x14;

    // VRAM allocation constants (from nx_init.cpp line 230, texture.cpp lines 377-381)
    private const uint VramBufferBaseInit = 0x2BC0;
    private const uint VramGroupSize = 0x0A20;
    private const uint VramToggle = 0x1E20;

    /// <summary>
    ///     Builds a mapping from (GroupChecksum, TBP, CBP) triples to texture checksums
    ///     by parsing a TEX file and simulating the PS2 VRAM allocation algorithm.
    ///     Group-aware keying prevents collisions from double-buffered VRAM banks
    ///     where different groups reuse the same TBP/CBP addresses.
    /// </summary>
    /// <returns>Dictionary mapping (GroupChecksum, TBP, CBP) → texture checksum.</returns>
    public static Dictionary<(uint Group, uint Tbp, uint Cbp), uint> BuildMapping(string texFilePath)
    {
        return BuildMapping(File.ReadAllBytes(texFilePath));
    }

    /// <summary>
    ///     Builds a mapping from (GroupChecksum, TBP, CBP) triples to texture checksums.
    /// </summary>
    public static Dictionary<(uint Group, uint Tbp, uint Cbp), uint> BuildMapping(byte[] data)
    {
        var mapping = new Dictionary<(uint, uint, uint), uint>();
        if (data.Length < 12) return mapping;

        var offset = 0;
        var version = ReadU32(data, ref offset);

        // Only handle TEX dictionary format (versions 3-5)
        if (version < 3 || version > 5) return mapping;

        var numGroups = ReadU32(data, ref offset);
        if (numGroups > 1000) return mapping;

        if (version >= 3)
            ReadU32(data, ref offset); // totalTextures

        var vramBufferBase = VramBufferBaseInit;

        // Max mip levels: 12 (4096→1 = 12 levels). Pre-allocate outside loops (CA2014).
        const int MaxMipLevels = 13;
#pragma warning disable S1481 // SonarAnalyzer false positive: Span<T> usage through indexing not tracked
        Span<uint> tbp = stackalloc uint[MaxMipLevels];
        Span<uint> numVramBytes = stackalloc uint[MaxMipLevels];
#pragma warning restore S1481

        for (var g = 0; g < numGroups; g++)
        {
            if (offset + 4 > data.Length) break;

            var groupChecksum = ReadU32(data, ref offset);
            if (version >= 2)
                ReadU32(data, ref offset); // groupFlags
            if (version >= 4)
                offset += 4; // groupPriority

            var numTextures = ReadU32(data, ref offset);
            if (numTextures > 10000) break;

            // Per-group VRAM region (texture.cpp lines 377-385)
            var vramStart = vramBufferBase;
            var vramEnd = vramBufferBase + VramGroupSize;
            vramBufferBase ^= VramToggle;

            var nextTbp = vramStart;
            var lastCbp = vramEnd;
            var texCount = 0u;

            for (var t = 0; t < numTextures; t++)
            {
                if (offset + 24 > data.Length) break;

                if (version >= 5)
                    ReadU32(data, ref offset); // per-texture flags

                var checksum = ReadU32(data, ref offset);
                var tw = ReadU32(data, ref offset);

                if (tw == 0xFFFFFFFF) continue; // skip entry

                var th = ReadU32(data, ref offset);
                var psm = ReadU32(data, ref offset);
                var cpsm = ReadU32(data, ref offset);
                var mxlRaw = (int)ReadU32(data, ref offset);

                // Duplicate reference — skip VRAM allocation but still skip file data
                var isDuplicate = mxlRaw < 0;
                var mxl = isDuplicate ? 0 : mxlRaw & 0xFF;

                // Skip past texture data in file
                if (!isDuplicate)
                {
                    offset = Align16(offset);
                    SkipTextureData(ref offset, (int)(1u << (int)tw), (int)(1u << (int)th),
                        psm, cpsm, mxl);
                }

                if (isDuplicate) continue;

                var bpp = GetBitsPerPixel(psm);

                // Compute dimensions per mip level (texture.cpp lines 524-532)
                var adjBpp = bpp == 24 ? 32u : bpp;
                var (pageW, pageH) = GetPageSize(psm);

                tbp.Clear();
                numVramBytes.Clear();

                for (var j = 0; j <= mxl; j++)
                {
                    var w = Math.Max((1u << (int)tw) >> j, 1u);
                    var h = Math.Max((1u << (int)th) >> j, 1u);
                    var tbw = (w + 63) >> 6;
                    if (bpp < 16 && tbw < 2) tbw = 2;

                    // Adjusted dimensions (texture.cpp lines 559-579)
                    var aw = w;
                    var ah = h;
                    if (aw < pageW && ah > pageH) aw = pageW;
                    if (aw > pageW && ah < pageH) ah = pageH;
                    if (tbw << 6 > aw) aw = tbw << 6;

                    numVramBytes[j] = (aw * ah * adjBpp) >> 3;
                }

                // Calculate TBP (texture.cpp lines 591-594)
                tbp[0] = nextTbp;
                for (var j = 1; j <= mxl; j++)
                    tbp[j] = (((tbp[j - 1] << 8) + numVramBytes[j - 1] + 0x1FFF) & 0xFFFFE000) >> 8;

                // Calculate CBP (texture.cpp lines 596-602)
                uint cbp;
                if (bpp >= 16)
                    cbp = lastCbp;
                else if (bpp == 4)
                    cbp = lastCbp - 1;
                else // 8-bit
                    cbp = lastCbp - (cpsm == PSMCT32 ? 4u : 2u);

                // Calculate next TBP (texture.cpp line 605)
                nextTbp = (((tbp[mxl] << 8) + numVramBytes[mxl] + 0x1FFF) & 0xFFFFE000) >> 8;

                // Bail if VRAM overpacked (texture.cpp lines 607-608)
                if (nextTbp > cbp)
                    break;

                lastCbp = cbp;

                // Cache optimization: odd-indexed small textures get +16 (texture.cpp lines 673-691)
                for (var j = 0; j <= mxl; j++)
                {
                    if ((texCount & 1) != 0)
                    {
                        var w = Math.Max((1u << (int)tw) >> j, 1u);
                        var h = Math.Max((1u << (int)th) >> j, 1u);

                        if (((bpp == 32 || bpp == 8) && w <= pageW >> 1 && h <= pageH)
                            || ((bpp == 16 || bpp == 4) && w <= pageW && h <= pageH >> 1))
                        {
                            tbp[j] += 16;
                        }
                    }

                    texCount++;
                }

                mapping[(groupChecksum, tbp[0], cbp)] = checksum;
            }
        }

        return mapping;
    }

    /// <summary>
    ///     Extracts (GroupChecksum, TBP, CBP) lookup key from a raw TEX0 GS register
    ///     value and the leaf's group checksum.
    /// </summary>
    public static (uint Group, uint Tbp, uint Cbp) DecodeTex0Key(ulong tex0, uint groupChecksum)
    {
        var tbp = (uint)(tex0 & 0x3FFF);
        var cbp = (uint)((tex0 >> 37) & 0x3FFF);
        return (groupChecksum, tbp, cbp);
    }

    private static uint GetBitsPerPixel(uint psm)
    {
        return psm switch
        {
            PSMCT32 => 32,
            PSMCT24 => 24,
            PSMCT16 => 16,
            PSMT8 => 8,
            PSMT4 => 4,
            _ => 32
        };
    }

    private static (uint Width, uint Height) GetPageSize(uint psm)
    {
        return psm switch
        {
            PSMCT32 or PSMCT24 => (64, 32),
            PSMCT16 => (64, 64),
            PSMT8 => (128, 64),
            PSMT4 => (128, 128),
            _ => (64, 32)
        };
    }

    private static void SkipTextureData(ref int offset, int width, int height,
        uint psm, uint cpsm, int mxl)
    {
        var bpp = GetBitsPerPixel(psm);

        // CLUT
        if (psm is PSMT8 or PSMT4)
        {
            var paletteSize = psm == PSMT8 ? 256 : 16;
            var clutBpp = cpsm == PSMCT32 ? 32u : 16u;
            offset += (int)(paletteSize * clutBpp / 8);
            offset = Align16(offset);
        }

        // Pixel data for all mip levels
        for (var j = 0; j <= mxl; j++)
        {
            var mw = Math.Max(width >> j, 1);
            var mh = Math.Max(height >> j, 1);
            offset += (int)(mw * mh * bpp / 8);
            offset = Align16(offset);
        }
    }

    private static uint ReadU32(byte[] data, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static int Align16(int offset)
    {
        return (offset + 15) & ~15;
    }
}
