using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parses THAW PS2 scene texture companion files (.tex.ps2, version 6).
///     NOT the standard Ps2TexFile format — uses GIF A+D register transfers
///     with embedded CLUT + pixel data.
///     <para>
///         Binary layout:
///         [0x00] u16 version (6), [0x04] u32 numTextures,
///         [0x08] u32 off1 (DMA chain), [0x0C] u32 off2 (pixel data),
///         [0x18] u32 modelChecksum.
///         Metadata (0x40..off1): variable-size per-texture entries with TEX0 register values.
///         DMA chain (off1..off2): GIF A+D blocks for GS CLUT/pixel upload.
///         Data (off2..EOF): CLUT + pixel data, sequential per unique texture.
///         Textures with duplicate checksums share data (stored once).
///         Some textures include mipmap data after mip-0 pixels.
///     </para>
/// </summary>
public static class ThawSceneTexFile
{
    private const int MaxExactTextureCount = 100;
    private const int MaxEmbeddedTextureCount = 200;

    /// <summary>Returns true if data looks like a THAW scene tex file (version 6).</summary>
    public static bool IsThawSceneTex(ReadOnlySpan<byte> data)
    {
        return TryReadHeader(data, MaxExactTextureCount, out _, out var off1, out _, false)
               && off1 > 0x40;
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
            return TryParseExact(data);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Parse a THAW scene texture file or a larger blob that embeds one or more
    ///     version-6 scene texture payloads.
    /// </summary>
    public static Ps2TexResult ParsePermissive(string filePath)
    {
        try
        {
            return ParsePermissive(File.ReadAllBytes(filePath));
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Parse exact version-6 files first, then fall back to scanning for embedded
    ///     version-6 payloads inside larger zone texture blobs.
    /// </summary>
    public static Ps2TexResult ParsePermissive(ReadOnlySpan<byte> data)
    {
        var exact = Parse(data);
        if (exact.Success)
            return exact;

        var textures = new List<Ps2Texture>();
        var byChecksum = new Dictionary<uint, int>();

        foreach (var offset in FindEmbeddedOffsets(data))
        {
            var slice = data[offset..];
            Ps2TexResult parsed;

            try
            {
                parsed = TryParseExact(slice);
            }
            catch
            {
                continue;
            }

            if (!parsed.Success || parsed.Textures.Count == 0)
                continue;

            foreach (var tex in parsed.Textures)
            {
                if (byChecksum.TryGetValue(tex.Checksum, out var existingIndex))
                {
                    if (textures[existingIndex].Pixels == null && tex.Pixels != null)
                        textures[existingIndex] = tex;
                    continue;
                }

                byChecksum[tex.Checksum] = textures.Count;
                textures.Add(tex);
            }
        }

        return textures.Count > 0
            ? new Ps2TexResult(textures)
            : exact;
    }

    /// <summary>
    ///     Builds a THAW scene-texture TEX0 map keyed by (TBP, CBP).
    ///     Used by world-zone MDL files that reference textures through DMA TEX0 state.
    /// </summary>
    public static Dictionary<(uint Tbp, uint Cbp), uint> BuildTbpCbpMap(ReadOnlySpan<byte> data)
    {
        var map = new Dictionary<(uint Tbp, uint Cbp), uint>();

        if (TryReadHeader(data, MaxExactTextureCount, out var numTex, out var off1, out _, false))
            MergeTbpCbpMap(data, numTex, off1, map);

        foreach (var offset in FindEmbeddedOffsets(data))
        {
            var slice = data[offset..];
            if (TryReadHeader(slice, MaxEmbeddedTextureCount, out var embeddedNumTex,
                    out var embeddedOff1, out _, false))
            {
                MergeTbpCbpMap(slice, embeddedNumTex, embeddedOff1, map);
            }
        }

        return map;
    }

    private static Ps2TexResult TryParseExact(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40)
            return Ps2TexResult.Fail("File too small for THAW scene tex");

        var version = BitConverter.ToUInt16(data);
        if (version != 6)
            return Ps2TexResult.Fail($"Version {version} (expected 6)");

        if (!TryReadHeader(data, MaxExactTextureCount, out var numTex, out var off1, out var off2))
            return Ps2TexResult.Fail("Invalid offsets");

        var entries = ScanTex0Entries(data, 0x40, off1, numTex);
        if (entries.Count == 0)
            return Ps2TexResult.Fail("No valid TEX0 entries found in metadata");

        var textures = DecodeEntries(data, entries, off2);
        return textures.Count > 0
            ? new Ps2TexResult(textures)
            : Ps2TexResult.Fail("No decodable textures found");
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, int maxTextures,
        out int numTex, out int off1, out int off2, bool requireOff2 = true)
    {
        numTex = 0;
        off1 = 0;
        off2 = 0;

        if (data.Length < 0x40)
            return false;

        if (BitConverter.ToUInt16(data) != 6)
            return false;

        numTex = (int)BitConverter.ToUInt32(data[4..]);
        off1 = (int)BitConverter.ToUInt32(data[8..]);
        off2 = (int)BitConverter.ToUInt32(data[0x0C..]);

        if (numTex <= 0 || numTex > maxTextures)
            return false;
        if (off1 <= 0x40 || off1 >= data.Length)
            return false;

        if (requireOff2)
            return off2 > off1 && off2 < data.Length;

        return off2 == 0 || (off2 > off1 && off2 < data.Length);
    }

    private static List<int> FindEmbeddedOffsets(ReadOnlySpan<byte> data)
    {
        var offsets = new List<int>();

        for (var off = 0; off + 0x40 <= data.Length; off += 8)
        {
            if (!TryReadHeader(data[off..], MaxEmbeddedTextureCount,
                    out var numTex, out var off1, out _, false))
            {
                continue;
            }

            var entries = ScanTex0Entries(data[off..], 0x40, off1, numTex);
            if (entries.Count == 0)
                continue;

            offsets.Add(off);
        }

        return offsets;
    }

    private static List<Ps2Texture> DecodeEntries(ReadOnlySpan<byte> data,
        List<Tex0Entry> entries, int dataPos)
    {
        var textures = new List<Ps2Texture>();
        var decoded = new Dictionary<uint, Ps2Texture>();

        foreach (var entry in entries)
        {
            if (decoded.TryGetValue(entry.Checksum, out var existing))
            {
                textures.Add(existing);
                continue;
            }

            Ps2Texture tex;
            try
            {
                tex = DecodeEntry(data, entry, ref dataPos);
            }
            catch
            {
                tex = new Ps2Texture(entry.Checksum, entry.Width, entry.Height, entry.Psm, entry.Cpsm, null);
            }

            textures.Add(tex);
            decoded[entry.Checksum] = tex;
        }

        return textures;
    }

    private static Ps2Texture DecodeEntry(ReadOnlySpan<byte> data, Tex0Entry entry, ref int dataPos)
    {
        var psm = entry.Psm;
        var cpsm = entry.Cpsm;
        var width = entry.Width;
        var height = entry.Height;

        byte[]? clut = null;
        var paletteEntries = Ps2TexPixelDecoder.GetPaletteSize(psm);
        if (paletteEntries > 0)
        {
            var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm);
            var clutBytes = paletteEntries * clutBpp / 8;
            if (dataPos + clutBytes > data.Length)
                return new Ps2Texture(entry.Checksum, width, height, psm, cpsm, null);

            clut = data.Slice(dataPos, clutBytes).ToArray();
            dataPos += clutBytes;

            if (psm == Ps2TexPixelDecoder.PSMT8)
                UnswizzleClutCsm1(clut, clutBpp / 8);
        }

        var bpp = Ps2TexPixelDecoder.GetBitsPerPixel(psm);
        var pixelBytes = width * height * bpp / 8;
        if (dataPos + pixelBytes > data.Length)
            return new Ps2Texture(entry.Checksum, width, height, psm, cpsm, null);

        var texData = data.Slice(dataPos, pixelBytes);
        dataPos += pixelBytes;

        for (var mip = 1; mip <= entry.MipCount; mip++)
        {
            var mipW = Math.Max(1, width >> mip);
            var mipH = Math.Max(1, height >> mip);
            var mipBytes = mipW * mipH * bpp / 8;
            if (mipBytes < 1 || dataPos + mipBytes > data.Length)
                break;
            dataPos += mipBytes;
        }

        if (psm == Ps2TexPixelDecoder.PSMT8 && width >= height)
            texData = Ps2TexSwizzle.UnswizzlePsmt8(texData, width, height);
        else if (psm == Ps2TexPixelDecoder.PSMT4)
            texData = Ps2TexSwizzle.UnswizzlePsmt4(texData, width, height);

        var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        return new Ps2Texture(entry.Checksum, width, height, psm, cpsm, pixels);
    }

    private static void MergeTbpCbpMap(ReadOnlySpan<byte> data,
        int expected, int off1, Dictionary<(uint Tbp, uint Cbp), uint> map)
    {
        foreach (var entry in ScanTex0Entries(data, 0x40, off1, expected))
            map.TryAdd((entry.Tbp, entry.Cbp), entry.Checksum);
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

            var cbp = (uint)((val >> 37) & 0x3FFF);
            entries.Add(new Tex0Entry(checksum, tbp, cbp, psm, cpsm, 1 << tw, 1 << th, mipCount));
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

    private readonly record struct Tex0Entry(
        uint Checksum,
        uint Tbp,
        uint Cbp,
        uint Psm,
        uint Cpsm,
        int Width,
        int Height,
        int MipCount);
}
