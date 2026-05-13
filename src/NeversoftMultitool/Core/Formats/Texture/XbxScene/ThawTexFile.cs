using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.XbxScene;

/// <summary>
///     Parses THAW PC texture dictionaries (.tex.wpc) with 0xABADD00D magic.
///     Format: header(magic:u32 + version:u8 + flag:u8 + count:u16), per texture:
///     magic:u32 + unknown:u32 + checksum:u32 + width:u16 + height:u16 +
///     resizedW:u16 + resizedH:u16 + mipCount:u8 + texelDepth:u8 +
///     compression:u8 + paletteDepth:u8 + [palette] + per-mip data.
///     Reference: NxTools fmt_thtex_import.py (isTHAW=True).
/// </summary>
public static class ThawTexFile
{
    private const uint Magic = 0xABADD00D;
    private const ushort MaxPlausibleTextureCount = 4096;

    /// <summary>Returns true if the data begins with the THAW TEX magic 0xABADD00D.</summary>
    public static bool IsThawTex(ReadOnlySpan<byte> data)
    {
        return data.Length >= 8 && BitConverter.ToUInt32(data) == Magic;
    }

    /// <summary>
    ///     Locate an embedded THAW TEX dictionary inside a larger container.
    ///     PAK-extracted THAW PC world texture blobs prepend a small header before the real dictionary.
    /// </summary>
    public static bool TryFindEmbeddedDictionaryOffset(ReadOnlySpan<byte> data, out int offset)
    {
        offset = 0;

        if (IsThawTex(data))
            return true;

        if (data.Length < 12)
            return false;

        for (var i = 0; i <= data.Length - 12; i += 4)
        {
            if (BitConverter.ToUInt32(data[i..]) != Magic)
                continue;

            var textureCount = BitConverter.ToUInt16(data[(i + 6)..]);
            if (textureCount == 0 || textureCount > MaxPlausibleTextureCount)
                continue;

            // A valid THAW dictionary begins immediately with a per-texture header.
            if (BitConverter.ToUInt32(data[(i + 8)..]) != Magic)
                continue;

            offset = i;
            return true;
        }

        return false;
    }

    /// <summary>Parse a THAW .tex.wpc file from disk.</summary>
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

    /// <summary>Parse a THAW .tex.wpc byte array and return all textures as RGBA32.</summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 8)
                return Ps2TexResult.Fail("File too small");

            var offset = 0;
            var allowPartial = false;
            if (!IsThawTex(data))
            {
                if (!TryFindEmbeddedDictionaryOffset(data, out offset))
                {
                    var magic = BitConverter.ToUInt32(data);
                    return Ps2TexResult.Fail($"Bad magic 0x{magic:X8} (expected 0xABADD00D)");
                }

                allowPartial = true;
                data = data[offset..];
            }

            return ParseDictionary(data, allowPartial);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    private static Ps2TexResult ParseDictionary(ReadOnlySpan<byte> data, bool allowPartial)
    {
        var textureCount = BitConverter.ToUInt16(data[6..]);
        var textures = new List<Ps2Texture>();
        var offset = 8;

        Ps2TexResult FailOrPartial(string message)
        {
            if (allowPartial && textures.Count > 0)
                return new Ps2TexResult(textures);

            return Ps2TexResult.Fail(message);
        }

        try
        {
            for (var i = 0; i < textureCount; i++)
            {
                if (offset + 24 > data.Length)
                    return FailOrPartial($"Truncated texture header at index {i}");

                // Per-texture header: magic(u32) + unknown(u32) = 8 bytes skip
                offset += 8;

                var checksum = BitConverter.ToUInt32(data[offset..]);
                offset += 4;
                var width = (int)BitConverter.ToUInt16(data[offset..]);
                offset += 2;
                var height = (int)BitConverter.ToUInt16(data[offset..]);
                offset += 2;
                // resizedWidth, resizedHeight (informational, skip)
                offset += 4;
                var mipCount = data[offset++];
                var texelDepth = data[offset++];
                var compression = data[offset++];
                var paletteDepth = data[offset++];

                // Normalize compression: 2→1 (DXT1), 3→5 (DXT5)
                if (compression == 2) compression = 1;
                if (compression == 3) compression = 5;

                // Read optional palette (only when uncompressed + has palette)
                byte[]? palette = null;
                if (compression == 0 && paletteDepth > 0)
                {
                    if (offset + 4 > data.Length)
                        return FailOrPartial($"Truncated palette header at texture {i}");

                    var paletteColorCount = (int)BitConverter.ToUInt32(data[offset..]);
                    offset += 4;

                    var paletteBytes = paletteColorCount * 4; // RGBA
                    if (offset + paletteBytes > data.Length)
                        return FailOrPartial($"Truncated palette data at texture {i}");

                    palette = data.Slice(offset, paletteBytes).ToArray();
                    offset += paletteBytes;
                }

                // Read mip levels (mip 0 = full resolution)
                byte[]? pixels = null;
                var mipW = width;
                var mipH = height;

                for (var m = 0; m < mipCount; m++)
                {
                    int dataSize;
                    if (compression != 0)
                    {
                        // DXT compressed: dataSize as u32
                        if (offset + 4 > data.Length)
                            return FailOrPartial($"Truncated mip header at texture {i}, mip {m}");
                        dataSize = (int)BitConverter.ToUInt32(data[offset..]);
                        offset += 4;
                    }
                    else
                    {
                        // Uncompressed/paletted: bytesPerLine(u16) + numLines(u16)
                        if (offset + 4 > data.Length)
                            return FailOrPartial($"Truncated mip header at texture {i}, mip {m}");
                        var bytesPerLine = (int)BitConverter.ToUInt16(data[offset..]);
                        var numLines = (int)BitConverter.ToUInt16(data[(offset + 2)..]);
                        dataSize = bytesPerLine * numLines;
                        offset += 4;
                    }

                    if (offset + dataSize > data.Length)
                        return FailOrPartial($"Truncated mip data at texture {i}, mip {m}");

                    if (m == 0)
                    {
                        pixels = DecodeMip(data.Slice(offset, dataSize),
                            mipW, mipH, texelDepth, compression, palette);

                        // THAW PC textures are stored bottom-up (PS2 heritage).
                        // Flip so standalone PNGs look right-side-up.
                        if (pixels != null)
                            FlipVertical(pixels, mipW, mipH);
                    }

                    offset += dataSize;
                    mipW = Math.Max(1, mipW >> 1);
                    mipH = Math.Max(1, mipH >> 1);
                }

                textures.Add(new Ps2Texture(
                    checksum, width, height, 0, 0, pixels, QbKey.QbKey.TryResolve(checksum)));
            }

            return new Ps2TexResult(textures);
        }
        catch (Exception ex)
        {
            return FailOrPartial(ex.Message);
        }
    }

    private static byte[]? DecodeMip(ReadOnlySpan<byte> data, int width, int height,
        int texelDepth, int compression, byte[]? palette)
    {
        return compression switch
        {
            1 => DxtDecoder.DecodeDxt1(data, width, height),
            5 => DxtDecoder.DecodeDxt5(data, width, height),
            0 when palette != null => DecodePaletted(data, width, height, texelDepth, palette),
            0 when texelDepth == 32 => DecodeBgra32(data, width, height),
            _ => null
        };
    }

    private static byte[] DecodePaletted(ReadOnlySpan<byte> data, int width, int height,
        int texelDepth, byte[] palette)
    {
        var output = new byte[width * height * 4];
        var paletteEntries = palette.Length / 4;

        if (texelDepth == 8)
        {
            for (var i = 0; i < width * height && i < data.Length; i++)
            {
                var idx = data[i];
                if (idx < paletteEntries)
                {
                    var pi = idx * 4;
                    var oi = i * 4;
                    output[oi] = palette[pi]; // R
                    output[oi + 1] = palette[pi + 1]; // G
                    output[oi + 2] = palette[pi + 2]; // B
                    output[oi + 3] = palette[pi + 3]; // A
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
                    output[oi] = palette[pi]; // R
                    output[oi + 1] = palette[pi + 1]; // G
                    output[oi + 2] = palette[pi + 2]; // B
                    output[oi + 3] = palette[pi + 3]; // A
                }
            }
        }

        return output;
    }

    private static void FlipVertical(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        var temp = new byte[rowBytes];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var topOff = top * rowBytes;
            var botOff = bottom * rowBytes;
            Buffer.BlockCopy(pixels, topOff, temp, 0, rowBytes);
            Buffer.BlockCopy(pixels, botOff, pixels, topOff, rowBytes);
            Buffer.BlockCopy(temp, 0, pixels, botOff, rowBytes);
        }
    }

    private static byte[] DecodeBgra32(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (var i = 0; i < width * height && (i + 1) * 4 <= data.Length; i++)
        {
            var si = i * 4;
            var oi = i * 4;
            output[oi] = data[si + 2];
            output[oi + 1] = data[si + 1];
            output[oi + 2] = data[si];
            output[oi + 3] = data[si + 3];
        }

        return output;
    }
}
