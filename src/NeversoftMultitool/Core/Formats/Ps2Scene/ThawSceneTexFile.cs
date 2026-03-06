using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parses THAW PS2 scene texture companion files (.tex.ps2, version 6).
///     NOT the standard Ps2TexFile format — uses GIF A+D register transfers
///     with embedded CLUT + pixel data.
///     <para>
///     Binary layout:
///     [0x00] u16 version (6), [0x04] u32 numTextures,
///     [0x08] u32 off1 (DMA chain), [0x0C] u32 off2 (pixel data),
///     [0x18] u32 modelChecksum.
///     Metadata (0x40..off1): variable-size per-texture entries with TEX0 register values.
///     DMA chain (off1..off2): GIF A+D blocks for GS CLUT/pixel upload.
///     Data (off2..EOF): CLUT + pixel data, sequential per unique texture.
///     Textures with duplicate checksums share data (stored once).
///     Some textures include mipmap data after mip-0 pixels.
///     </para>
/// </summary>
public static class ThawSceneTexFile
{
    /// <summary>Returns true if data looks like a THAW scene tex file (version 6).</summary>
    public static bool IsThawSceneTex(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return false;
        var version = BitConverter.ToUInt16(data);
        if (version != 6) return false;
        var numTex = BitConverter.ToUInt32(data[4..]);
        if (numTex == 0 || numTex > 100) return false;
        var off1 = BitConverter.ToUInt32(data[8..]);
        return off1 > 0x40 && off1 < (uint)data.Length;
    }

    /// <summary>Parse a THAW .tex.ps2 scene texture file from disk.</summary>
    public static Ps2TexResult Parse(string filePath)
    {
        try
        {
            return Parse(File.ReadAllBytes(filePath));
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>Parse a THAW .tex.ps2 scene texture byte array.</summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 0x40)
                return Ps2TexResult.Fail("File too small for THAW scene tex");

            var version = BitConverter.ToUInt16(data);
            if (version != 6)
                return Ps2TexResult.Fail($"Version {version} (expected 6)");

            var numTex = (int)BitConverter.ToUInt32(data[4..]);
            var off1 = (int)BitConverter.ToUInt32(data[8..]);
            var off2 = (int)BitConverter.ToUInt32(data[0x0C..]);

            if (numTex <= 0 || numTex > 100)
                return Ps2TexResult.Fail($"Invalid texture count {numTex}");
            if (off1 <= 0x40 || off1 >= data.Length || off2 <= off1 || off2 >= data.Length)
                return Ps2TexResult.Fail("Invalid offsets");

            // Scan metadata for TEX0 register values (8-byte aligned, valid PSM, TBP >= 0x2BC0).
            // Checksum is at TEX0_offset - 0x10 and must be > 0xFFFF (filter false positives).
            var entries = ScanTex0Entries(data, 0x40, off1, numTex);
            if (entries.Count == 0)
                return Ps2TexResult.Fail("No valid TEX0 entries found in metadata");

            // Read CLUT + pixel data from off2, sequential per unique texture.
            // Duplicate checksums share data (stored once).
            var textures = new List<Ps2Texture>();
            var decoded = new Dictionary<uint, Ps2Texture>();
            var dataPos = off2;

            foreach (var entry in entries)
            {
                if (decoded.TryGetValue(entry.Checksum, out var existing))
                {
                    textures.Add(existing);
                    continue;
                }

                var psm = entry.Psm;
                var cpsm = entry.Cpsm;
                var width = entry.Width;
                var height = entry.Height;

                // Read CLUT
                byte[]? clut = null;
                var paletteEntries = Ps2TexPixelDecoder.GetPaletteSize(psm);
                if (paletteEntries > 0)
                {
                    var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm);
                    var clutBytes = paletteEntries * clutBpp / 8;
                    if (dataPos + clutBytes > data.Length)
                    {
                        textures.Add(new Ps2Texture(entry.Checksum, width, height, psm, cpsm, null));
                        decoded[entry.Checksum] = textures[^1];
                        continue;
                    }

                    clut = data.Slice(dataPos, clutBytes).ToArray();
                    dataPos += clutBytes;

                    // THAW scene tex CLUTs are pre-swizzled for GIF transfer (CSM1 order).
                    // PSMT8 needs unswizzle; PSMT4 does not.
                    if (psm == Ps2TexPixelDecoder.PSMT8)
                        UnswizzleClutCsm1(clut, clutBpp / 8);
                }

                // Read pixel data (mip 0 only)
                var bpp = Ps2TexPixelDecoder.GetBitsPerPixel(psm);
                var pixelBytes = width * height * bpp / 8;
                if (dataPos + pixelBytes > data.Length)
                {
                    textures.Add(new Ps2Texture(entry.Checksum, width, height, psm, cpsm, null));
                    decoded[entry.Checksum] = textures[^1];
                    continue;
                }

                ReadOnlySpan<byte> texData = data.Slice(dataPos, pixelBytes);
                dataPos += pixelBytes;

                // Skip mipmap data (count from metadata at TEX0_offset - 0x08)
                for (var mip = 1; mip <= entry.MipCount; mip++)
                {
                    var mipW = Math.Max(1, width >> mip);
                    var mipH = Math.Max(1, height >> mip);
                    var mipBytes = mipW * mipH * bpp / 8;
                    if (mipBytes < 1 || dataPos + mipBytes > data.Length)
                        break;
                    dataPos += mipBytes;
                }

                // Apply GS cross-format pixel unswizzle (Conv8to32/Conv4to32/Conv4to16).
                // Only dimensions in the sCanConvert tables qualify — others are linear.
                // PSMT8: Conv8to32 for 32×32, 64×64, >=128×128; else linear.
                // PSMT4: Conv4to32 or Conv4to16 (handled internally by UnswizzlePsmt4).
                if (psm == Ps2TexPixelDecoder.PSMT8 && Ps2TexSwizzle.CanConv8to32(width, height))
                    texData = Ps2TexSwizzle.UnswizzlePsmt8(texData, width, height);
                else if (psm == Ps2TexPixelDecoder.PSMT4)
                    texData = Ps2TexSwizzle.UnswizzlePsmt4(texData, width, height);

                var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
                var tex = new Ps2Texture(entry.Checksum, width, height, psm, cpsm, pixels);
                textures.Add(tex);
                decoded[entry.Checksum] = tex;
            }

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Scans the metadata region for TEX0 register values.
    ///     TEX0 is identified by: 8-byte aligned, valid PSM (0x00-0x14 known),
    ///     TBP >= 0x2BC0 (VramBufferBase), TBW >= 1, TW/TH 1-10.
    ///     Texture checksum at TEX0_offset - 0x10, must be > 0xFFFF.
    /// </summary>
    private static List<Tex0Entry> ScanTex0Entries(ReadOnlySpan<byte> data, int start, int end, int expected)
    {
        var entries = new List<Tex0Entry>();

        for (var off = start; off + 8 <= end; off += 8)
        {
            var val = BitConverter.ToUInt64(data[off..]);
            var tbp = (uint)(val & 0x3FFF);
            var tbw = (uint)((val >> 14) & 0x3F);
            var psm = (uint)((val >> 20) & 0x3F);
            var tw = (int)((val >> 26) & 0xF);
            var th = (int)((val >> 30) & 0xF);
            var cpsm = (uint)((val >> 51) & 0xF);

            if (!Ps2TexPixelDecoder.IsValidPsm(psm)) continue;
            if (tw < 1 || tw > 10 || th < 1 || th > 10) continue;
            if (tbp < 0x2BC0 || tbw < 1) continue;

            // Checksum at TEX0 - 0x10, mip count at TEX0 - 0x08
            var ckOff = off - 0x10;
            if (ckOff < start) continue;
            var checksum = BitConverter.ToUInt32(data[ckOff..]);
            if (checksum <= 0xFFFF) continue;

            var mipOff = off - 0x08;
            var mipCount = mipOff >= start ? (int)BitConverter.ToUInt32(data[mipOff..]) : 0;
            if (mipCount is < 0 or > 7) mipCount = 0; // Sanity clamp

            entries.Add(new Tex0Entry(checksum, psm, cpsm, 1 << tw, 1 << th, mipCount));
        }

        // If we found more than expected (mipmap TEX0s slipping through),
        // take only the first 'expected' entries since they appear in order.
        if (entries.Count > expected)
            entries = entries.GetRange(0, expected);

        return entries;
    }

    /// <summary>
    ///     Applies CSM1 CLUT unswizzle for PSMT8 (256 entries).
    ///     Within each group of 32 entries, swaps entries at positions 8-15 with 16-23.
    /// </summary>
    private static void UnswizzleClutCsm1(byte[] clut, int entrySize)
    {
        for (var group = 0; group < 8; group++)
        {
            for (var i = 0; i < 8; i++)
            {
                var posA = (group * 32 + 8 + i) * entrySize;
                var posB = (group * 32 + 16 + i) * entrySize;
                if (posA + entrySize > clut.Length || posB + entrySize > clut.Length)
                    return;
                for (var j = 0; j < entrySize; j++)
                    (clut[posA + j], clut[posB + j]) = (clut[posB + j], clut[posA + j]);
            }
        }
    }

    private readonly record struct Tex0Entry(uint Checksum, uint Psm, uint Cpsm, int Width, int Height, int MipCount);
}
