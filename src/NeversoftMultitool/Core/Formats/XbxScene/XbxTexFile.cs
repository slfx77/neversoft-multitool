using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parses Xbox/PC TEX texture dictionaries (.tex.xbx) from THUG2.
///     Format: version(u32=1) + num_textures(u32), then per-texture:
///     32B header (checksum, w, h, levels, texel_depth, pal_depth, dxt_version, pal_size)
///     + optional palette (pal_size bytes, BGRA) + per-mip (data_size:u32 + data).
///     DXT versions: 0=raw/paletted, 1/2=DXT1, 5=DXT5.
/// </summary>
public static class XbxTexFile
{
    /// <summary>Returns true if the data begins with a valid Xbox TEX header (version 1).</summary>
    public static bool IsTexFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        var version = BitConverter.ToUInt32(data);
        return version == 1;
    }

    /// <summary>Parse a .tex.xbx file and return all textures as RGBA32 pixel data.</summary>
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

    /// <summary>Parse a .tex.xbx byte array and return all textures as RGBA32 pixel data.</summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 8)
                return Ps2TexResult.Fail("File too small");

            var version = BitConverter.ToUInt32(data);
            if (version != 1)
                return Ps2TexResult.Fail($"Unsupported TEX version {version} (expected 1)");

            var numTextures = BitConverter.ToUInt32(data[4..]);
            var textures = new List<Ps2Texture>();
            var offset = 8;

            for (var i = 0; i < numTextures; i++)
            {
                if (offset + 32 > data.Length)
                    return Ps2TexResult.Fail($"Truncated texture header at index {i}");

                var checksum = BitConverter.ToUInt32(data[offset..]);
                var width = (int)BitConverter.ToUInt32(data[(offset + 4)..]);
                var height = (int)BitConverter.ToUInt32(data[(offset + 8)..]);
                var levels = (int)BitConverter.ToUInt32(data[(offset + 12)..]);
                var texelDepth = (int)BitConverter.ToUInt32(data[(offset + 16)..]);
                // offset+20 = palDepth (unused, palette format always BGRA32)
                var dxtVersion = (int)BitConverter.ToUInt32(data[(offset + 24)..]);
                var palSize = (int)BitConverter.ToUInt32(data[(offset + 28)..]);
                offset += 32;

                // Read optional palette
                byte[]? palette = null;
                if (palSize > 0)
                {
                    if (offset + palSize > data.Length)
                        return Ps2TexResult.Fail($"Truncated palette at texture {i}");
                    palette = data.Slice(offset, palSize).ToArray();
                    offset += palSize;
                }

                // Read mip 0 (largest level) — skip remaining mips
                byte[]? pixels = null;
                for (var mip = 0; mip < levels; mip++)
                {
                    if (offset + 4 > data.Length)
                        return Ps2TexResult.Fail($"Truncated mip header at texture {i}, mip {mip}");

                    var dataSize = (int)BitConverter.ToUInt32(data[offset..]);
                    offset += 4;

                    if (offset + dataSize > data.Length)
                        return Ps2TexResult.Fail($"Truncated mip data at texture {i}, mip {mip}");

                    if (mip == 0)
                    {
                        pixels = DecodeMip(data.Slice(offset, dataSize), width, height,
                            texelDepth, dxtVersion, palette);
                    }

                    offset += dataSize;
                }

                textures.Add(new Ps2Texture(
                    checksum, width, height, 0, 0, pixels, QbKey.TryResolve(checksum)));
            }

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>Save all parsed textures to PNG files. Returns the count written.</summary>
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

    private static byte[]? DecodeMip(ReadOnlySpan<byte> data, int width, int height,
        int texelDepth, int dxtVersion, byte[]? palette)
    {
        return dxtVersion switch
        {
            1 => DxtDecoder.DecodeDxt1(data, width, height),
            2 => DxtDecoder.DecodeDxt3(data, width, height),
            5 => DxtDecoder.DecodeDxt5(data, width, height),
            0 when palette != null => DecodePaletted(data, width, height, texelDepth, palette),
            0 when texelDepth == 32 => DecodeBgra32(data, width, height),
            0 when texelDepth == 16 => DecodeArgb1555(data, width, height),
            _ => null // unknown format
        };
    }

    private static byte[] DecodePaletted(ReadOnlySpan<byte> data, int width, int height,
        int texelDepth, byte[] palette)
    {
        var output = new byte[width * height * 4];
        var paletteEntries = palette.Length / 4; // BGRA entries

        if (texelDepth == 8)
        {
            for (var i = 0; i < width * height && i < data.Length; i++)
            {
                var idx = data[i];
                if (idx < paletteEntries)
                {
                    var pi = idx * 4;
                    var oi = i * 4;
                    output[oi] = palette[pi + 2]; // B→R
                    output[oi + 1] = palette[pi + 1]; // G→G
                    output[oi + 2] = palette[pi]; // R→B
                    output[oi + 3] = palette[pi + 3]; // A→A
                }
            }
        }
        else if (texelDepth == 4)
        {
            for (var i = 0; i < width * height; i++)
            {
                var byteIdx = i / 2;
                if (byteIdx >= data.Length) break;
                var idx = (i & 1) == 0 ? data[byteIdx] & 0x0F : data[byteIdx] >> 4;
                if (idx < paletteEntries)
                {
                    var pi = idx * 4;
                    var oi = i * 4;
                    output[oi] = palette[pi + 2]; // B→R
                    output[oi + 1] = palette[pi + 1]; // G→G
                    output[oi + 2] = palette[pi]; // R→B
                    output[oi + 3] = palette[pi + 3]; // A→A
                }
            }
        }

        return output;
    }

    private static byte[] DecodeBgra32(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (var i = 0; i < width * height && (i + 1) * 4 <= data.Length; i++)
        {
            var si = i * 4;
            var oi = i * 4;
            output[oi] = data[si + 2]; // B→R
            output[oi + 1] = data[si + 1]; // G→G
            output[oi + 2] = data[si]; // R→B
            output[oi + 3] = data[si + 3]; // A→A
        }

        return output;
    }

    private static byte[] DecodeArgb1555(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (var i = 0; i < width * height && (i + 1) * 2 <= data.Length; i++)
        {
            var val = BitConverter.ToUInt16(data[(i * 2)..]);
            var oi = i * 4;
            var r = (val >> 10) & 0x1F;
            var g = (val >> 5) & 0x1F;
            var b = val & 0x1F;
            var a = (val >> 15) & 1;
            output[oi] = (byte)((r << 3) | (r >> 2));
            output[oi + 1] = (byte)((g << 3) | (g >> 2));
            output[oi + 2] = (byte)((b << 3) | (b >> 2));
            output[oi + 3] = (byte)(a * 255);
        }

        return output;
    }
}
