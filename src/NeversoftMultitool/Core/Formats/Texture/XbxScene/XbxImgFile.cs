using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parses Xbox/PC IMG texture files (.img.xbx) from THUG2.
///     Format: 32B header (version=2, unk, width, height, format, unk, pitch_w:u16, pitch_h:u16, clut_size)
///     + optional BGRA palette (clut_size bytes) + pixel data.
///     Paletted textures use Xbox morton swizzle for pixel addressing.
/// </summary>
public static class XbxImgFile
{
    /// <summary>Returns true if the data begins with a valid Xbox IMG header (version 2).</summary>
    public static bool IsImgFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return false;
        var version = BitConverter.ToUInt32(data);
        return version == 2;
    }

    /// <summary>Parse a .img.xbx file and return the texture as RGBA32 pixel data.</summary>
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

    /// <summary>Parse a .img.xbx byte array and return the texture as RGBA32 pixel data.</summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 32)
                return Ps2TexResult.Fail("File too small");

            var version = BitConverter.ToUInt32(data);
            if (version != 2)
                return Ps2TexResult.Fail($"Unsupported IMG version {version} (expected 2)");

            // Header: version(4) + unk(4) + width(4) + height(4) + format(4) + unk(4) + pitch_w(2) + pitch_h(2) + clut_size(4) = 32 bytes
            var width = (int)BitConverter.ToUInt32(data[8..]);
            var height = (int)BitConverter.ToUInt32(data[12..]);
            // offset+16 = format (D3DFMT enum, unused — we detect from clut_size)
            var pitchW = BitConverter.ToUInt16(data[24..]);
            var pitchH = BitConverter.ToUInt16(data[26..]);
            var clutSize = (int)BitConverter.ToUInt32(data[28..]);
            var offset = 32;

            // Dimensions sanity check
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Ps2TexResult.Fail($"Invalid dimensions {width}x{height}");

            byte[]? pixels;

            if (clutSize > 0)
            {
                // Paletted: read BGRA palette, then 8-bit indexed pixels with morton swizzle
                if (offset + clutSize > data.Length)
                    return Ps2TexResult.Fail("Truncated palette");

                var palette = data.Slice(offset, clutSize).ToArray();
                offset += clutSize;

                // Use pitch dimensions for pixel data stride (may be padded)
                var pw = pitchW > 0 ? pitchW : width;
                var ph = pitchH > 0 ? pitchH : height;
                var pixelCount = pw * ph;

                if (offset + pixelCount > data.Length)
                    return Ps2TexResult.Fail("Truncated pixel data");

                var indexData = data.Slice(offset, pixelCount);
                pixels = DecodePalettedSwizzle(indexData, width, height, pw, ph, palette);
            }
            else
            {
                // Raw BGRA32
                var pixelBytes = width * height * 4;
                if (offset + pixelBytes > data.Length)
                    return Ps2TexResult.Fail("Truncated pixel data");

                pixels = DecodeBgra32(data.Slice(offset, pixelBytes), width, height);
            }

            var tex = new Ps2Texture(0, width, height, 0, 0, pixels);

            return new Ps2TexResult([tex]);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>Save the parsed texture to a PNG file. Returns 1 if written, 0 otherwise.</summary>
    public static int SaveAsPng(Ps2TexResult result, string outputPath)
    {
        if (!result.Success || result.Textures.Count == 0) return 0;

        var tex = result.Textures[0];
        if (tex.Pixels == null) return 0;

        ImageWriter.WritePng(outputPath, tex.Width, tex.Height, tex.Pixels);
        return 1;
    }

    /// <summary>
    ///     Decode paletted pixels with Xbox morton (Z-order) swizzle.
    ///     The pixel data is stored in morton order for GPU-efficient access.
    /// </summary>
    private static byte[] DecodePalettedSwizzle(ReadOnlySpan<byte> data,
        int width, int height, int pitchW, int pitchH, byte[] palette)
    {
        var output = new byte[width * height * 4];
        var paletteEntries = palette.Length / 4;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Morton/Z-order curve: interleave bits of x and y
                var swizzledIndex = MortonIndex(x, y, pitchW, pitchH);
                if (swizzledIndex >= data.Length) continue;

                var idx = data[swizzledIndex];
                if (idx >= paletteEntries) continue;

                var pi = idx * 4;
                var oi = (y * width + x) * 4;
                output[oi] = palette[pi + 2]; // B→R
                output[oi + 1] = palette[pi + 1]; // G→G
                output[oi + 2] = palette[pi]; // R→B
                output[oi + 3] = palette[pi + 3]; // A→A
            }
        }

        return output;
    }

    /// <summary>
    ///     Calculate morton (Z-order) index by interleaving bits of x and y.
    ///     This is the standard Xbox texture swizzle pattern.
    /// </summary>
    private static int MortonIndex(int x, int y, int width, int height)
    {
        // Build bit masks for x and y based on the smaller dimension
        var index = 0;
        var bit = 1;
        int maskX = 0, maskY = 0;
        int w = width, h = height;

        while (w > 1 || h > 1)
        {
            if (w > 1)
            {
                maskX |= bit;
                bit <<= 1;
                w >>= 1;
            }

            if (h > 1)
            {
                maskY |= bit;
                bit <<= 1;
                h >>= 1;
            }
        }

        // Spread x bits into maskX positions
        var spreadX = SpreadBits(x, maskX);
        var spreadY = SpreadBits(y, maskY);
        index = spreadX | spreadY;

        return index;
    }

    /// <summary>
    ///     Spread the bits of value into the positions indicated by mask.
    ///     E.g., if mask = 0b010101 and value = 0b111, result = 0b010101.
    /// </summary>
    private static int SpreadBits(int value, int mask)
    {
        var result = 0;
        var valueBit = 1;

        for (var bit = 1; bit != 0 && mask != 0; bit <<= 1)
        {
            if ((mask & bit) != 0)
            {
                if ((value & valueBit) != 0)
                    result |= bit;
                valueBit <<= 1;
            }
        }

        return result;
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
}
