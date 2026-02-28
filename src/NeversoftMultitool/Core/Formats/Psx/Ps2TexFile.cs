namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Parses PS2 TEX and IMG texture files to extract textures as RGBA pixel data.
///     TEX files (versions 3-5): Multi-texture dictionary with groups (THPS4, THUG, THUG2).
///     IMG files (version 2): Single-texture loadscreen format (THPS4, THUG, THUG2).
///     Format from THUG source: Gfx/NGPS/NX/texture.cpp, gs.h.
/// </summary>
public static class Ps2TexFile
{
    // MXL bit 30 signals GS-swizzled pixel data (THUG2+).
    // Pixel indices are stored in PS2 GS VRAM tiled layout rather than linear scan-line order.
    private const int MXL_FLAG_GS_SWIZZLED = 0x40000000;

    /// <summary>
    ///     Parses a PS2 TEX or IMG file and returns all extracted textures.
    /// </summary>
    public static Ps2TexResult Parse(string filePath)
    {
        try
        {
            var data = File.ReadAllBytes(filePath);
            if (data.Length < 8)
                return Ps2TexResult.Fail("File too small");

            var version = BitConverter.ToUInt32(data, 0);

            return version switch
            {
                2 => ParseImg(data),
                3 or 4 or 5 => ParseTex(data, (int)version),
                0x0016 => RwTxdFile.Parse(data), // RenderWare TXD (THPS3 PS2)
                _ => Ps2TexResult.Fail($"Unsupported version {version} (expected 2-5)")
            };
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Returns a human-readable description of a pixel storage mode.
    /// </summary>
    public static string DescribePsm(uint psm)
    {
        return Ps2TexPixelDecoder.DescribePsm(psm);
    }

    /// <summary>
    ///     Saves all parsed textures to PNG files in the output directory.
    ///     Returns the number of textures written.
    /// </summary>
    public static int SaveAllAsPng(Ps2TexResult result, string outputDir, string stem)
    {
        if (!result.Success) return 0;

        var count = 0;
        foreach (var tex in result.Textures)
        {
            if (tex.Pixels == null) continue;

            var name = tex.Name ?? QbKey.TryResolve(tex.Checksum) ?? $"{tex.Checksum:X8}";
            var path = Path.Combine(outputDir, stem, $"{name}.png");
            ImageWriter.WritePng(path, tex.Width, tex.Height, tex.Pixels);
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Parses a TEX dictionary file (versions 3-5).
    /// </summary>
    private static Ps2TexResult ParseTex(byte[] data, int version)
    {
        var offset = 4; // skip version
        if (offset + 4 > data.Length) return Ps2TexResult.Fail("Truncated header");

        var numGroups = ReadU32(data, ref offset);
        if (numGroups > 1000) return Ps2TexResult.Fail($"Invalid group count {numGroups}");

        if (version >= 3)
            ReadU32(data, ref offset); // totalTextures (informational)

        var textures = new List<Ps2Texture>();

        for (var g = 0; g < numGroups; g++)
        {
            if (offset + 4 > data.Length) break;

            ReadU32(data, ref offset); // groupChecksum
            if (version >= 2)
                ReadU32(data, ref offset); // groupFlags
            if (version >= 4)
                offset += 4; // skip groupPriority (float)

            var numTextures = ReadU32(data, ref offset);
            if (numTextures > 10000) return Ps2TexResult.Fail($"Invalid texture count {numTextures} in group {g}");

            for (var t = 0; t < numTextures; t++)
            {
                if (offset + 24 > data.Length) break;

                if (version >= 5)
                    ReadU32(data, ref offset); // per-texture flags

                var checksum = ReadU32(data, ref offset);
                var tw = ReadU32(data, ref offset);
                var th = ReadU32(data, ref offset);

                // TW=0xFFFFFFFF means skip
                if (tw == 0xFFFFFFFF)
                    continue;

                var psm = ReadU32(data, ref offset);
                var cpsm = ReadU32(data, ref offset);
                var mxlRaw = (int)ReadU32(data, ref offset);

                var width = (int)(1u << (int)tw);
                var height = (int)(1u << (int)th);

                // Duplicate texture reference (negative MXL = bit 31 set = shares data with another texture)
                if (mxlRaw < 0)
                {
                    textures.Add(new Ps2Texture(checksum, width, height, psm, cpsm, null));
                    continue;
                }

                // Upper bits of MXL are flags; lower bits = mip level count
                var gsSwizzled = (mxlRaw & MXL_FLAG_GS_SWIZZLED) != 0;
                var mxl = mxlRaw & 0xFF;

                // Align to 16 bytes
                offset = Align16(offset);

                var pixels = ReadTextureData(data, ref offset, width, height, psm, cpsm, mxl, gsSwizzled);
                textures.Add(new Ps2Texture(checksum, width, height, psm, cpsm, pixels));
            }
        }

        return new Ps2TexResult(textures);
    }

    /// <summary>
    ///     Parses an IMG single-texture file (version 2).
    ///     Format: version(u32), checksum(u32), TW(u32), TH(u32), PSM(u32), CPSM(u32),
    ///     MXL(u32), orig_width(u16), orig_height(u16), [pad to 16], CLUT, pixels.
    ///     From sprite.cpp InitTexture(): pixel data is stored at orig_width x orig_height,
    ///     NOT at (1&lt;&lt;TW) x (1&lt;&lt;TH).
    /// </summary>
    private static Ps2TexResult ParseImg(byte[] data)
    {
        if (data.Length < 32) return Ps2TexResult.Fail("IMG file too small");

        var offset = 4; // skip version
        var checksum = ReadU32(data, ref offset);
        var tw = ReadU32(data, ref offset);
        var th = ReadU32(data, ref offset);
        var psm = ReadU32(data, ref offset);
        var cpsm = ReadU32(data, ref offset);
        ReadU32(data, ref offset); // MXL (always 0 for IMG, per Dbg_Assert)

        // Actual pixel dimensions (may differ from 1<<TW / 1<<TH for non-power-of-2 loadscreens)
        var origWidth = BitConverter.ToUInt16(data, offset);
        offset += 2;
        var origHeight = BitConverter.ToUInt16(data, offset);
        offset += 2;

        // Validate
        if (tw > 11 || th > 11) return Ps2TexResult.Fail($"Invalid dimensions TW={tw} TH={th}");
        if (!Ps2TexPixelDecoder.IsValidPsm(psm)) return Ps2TexResult.Fail($"Invalid PSM 0x{psm:X2}");

        // Use orig dimensions if present, fall back to power-of-2
        var width = origWidth > 0 ? origWidth : (int)(1u << (int)tw);
        var height = origHeight > 0 ? origHeight : (int)(1u << (int)th);

        // Align to 16 (header is 32 bytes -> already aligned)
        offset = Align16(offset);

        var pixels = ReadTextureData(data, ref offset, width, height, psm, cpsm, 0, false);
        if (pixels == null) return Ps2TexResult.Fail("Failed to decode pixel data");

        return new Ps2TexResult([new Ps2Texture(checksum, width, height, psm, cpsm, pixels)]);
    }

    /// <summary>
    ///     Reads CLUT + pixel data and returns decoded RGBA pixels.
    ///     Only reads mip level 0 (full resolution).
    /// </summary>
    private static byte[]? ReadTextureData(byte[] data, ref int offset, int width, int height,
        uint psm, uint cpsm, int mxl, bool gsSwizzled)
    {
        try
        {
            byte[]? clut = null;
            var paletteSize = Ps2TexPixelDecoder.GetPaletteSize(psm);

            if (paletteSize > 0)
            {
                var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm);
                var clutBytes = paletteSize * clutBpp / 8;
                if (offset + clutBytes > data.Length) return null;

                clut = new byte[clutBytes];
                Array.Copy(data, offset, clut, 0, clutBytes);
                offset += clutBytes;

                // Note: the engine applies CSM1 CLUT swizzle (texture.cpp:503) to rearrange
                // entries for GS VRAM upload, but file pixel indices are sequential (CSM0).
                // For extraction we use the CLUT as-is -- no swizzle needed.

                // Align after CLUT
                offset = Align16(offset);
            }

            // Read mip level 0 only
            var bpp = Ps2TexPixelDecoder.GetBitsPerPixel(psm);
            var texBytes = width * height * bpp / 8;
            if (offset + texBytes > data.Length) return null;

            ReadOnlySpan<byte> texData = data.AsSpan(offset, texBytes);
            offset += texBytes;

            // Skip remaining mip levels
            for (var m = 1; m <= mxl; m++)
            {
                var mipW = Math.Max(1, width >> m);
                var mipH = Math.Max(1, height >> m);
                var mipBytes = mipW * mipH * bpp / 8;
                offset += mipBytes;
            }

            // THUG2+ stores paletted pixel data in PS2 GS VRAM tiled layout (MXL bit 30).
            // Un-swizzle to linear scan-line order before decoding.
            if (gsSwizzled)
            {
                if (psm == Ps2TexPixelDecoder.PSMT8)
                    texData = Ps2TexSwizzle.UnswizzlePsmt8(texData, width, height);
                else if (psm == Ps2TexPixelDecoder.PSMT4)
                    texData = Ps2TexSwizzle.UnswizzlePsmt4(texData, width, height);
            }

            return Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        }
        catch
        {
            return null;
        }
    }

    private static uint ReadU32(byte[] data, ref int offset)
    {
        var val = BitConverter.ToUInt32(data, offset);
        offset += 4;
        return val;
    }

    private static int Align16(int offset)
    {
        return (offset + 15) & ~15;
    }
}

public sealed record Ps2Texture(
    uint Checksum,
    int Width,
    int Height,
    uint Psm,
    uint Cpsm,
    byte[]? Pixels,
    string? Name = null);

public sealed class Ps2TexResult
{
    public Ps2TexResult(List<Ps2Texture> textures)
    {
        Success = true;
        Textures = textures;
    }

    private Ps2TexResult()
    {
    }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<Ps2Texture> Textures { get; init; } = [];

    public static Ps2TexResult Fail(string message)
    {
        return new Ps2TexResult { ErrorMessage = message };
    }
}
