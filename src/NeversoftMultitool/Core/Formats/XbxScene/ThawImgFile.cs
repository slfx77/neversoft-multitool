using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parses THAW PC single-texture IMG files (.img.wpc) with 0xABADD00D magic.
///     Layout is the single-texture counterpart to THAW .tex.wpc:
///     magic:u32 + version:u8 + flag:u8 + header_size:u16 +
///     checksum:u32 + width:u16 + height:u16 + resizedW:u16 + resizedH:u16 +
///     mipCount:u8 + texelDepth:u8 + compression:u8 + paletteDepth:u8 +
///     [palette header + palette] + per-mip data.
/// </summary>
public static class ThawImgFile
{
    private const uint Magic = 0xABADD00D;
    private const ushort HeaderSize = 0x14;

    /// <summary>Returns true if the data begins with the THAW IMG magic/header pair.</summary>
    public static bool IsThawImg(ReadOnlySpan<byte> data)
    {
        return data.Length >= 28
               && BitConverter.ToUInt32(data) == Magic
               && data[4] == 2
               && data[5] == 0
               && BitConverter.ToUInt16(data[6..]) == HeaderSize;
    }

    /// <summary>Parse a THAW .img.wpc file from disk.</summary>
    public static Ps2TexResult Parse(string filePath)
    {
        try
        {
            var result = Parse(File.ReadAllBytes(filePath));
            if (!result.Success || result.Textures.Count == 0)
                return result;

            var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath));
            return new Ps2TexResult(result.Textures
                .Select(texture => texture with { Name = stem })
                .ToList());
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>Parse a THAW .img.wpc byte array and return the texture as RGBA32 pixel data.</summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 28)
                return Ps2TexResult.Fail("File too small");

            if (!IsThawImg(data))
                return Ps2TexResult.Fail("Unsupported THAW IMG header");

            var checksum = BitConverter.ToUInt32(data[8..]);
            var width = (int)BitConverter.ToUInt16(data[12..]);
            var height = (int)BitConverter.ToUInt16(data[14..]);
            var mipCount = data[20];
            var texelDepth = data[21];
            var compression = data[22];
            var paletteDepth = data[23];

            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Ps2TexResult.Fail($"Invalid dimensions {width}x{height}");
            if (mipCount == 0)
                return Ps2TexResult.Fail("IMG has no mip levels");

            // THAW PC uses the same normalization as .tex.wpc: 2->DXT1, 3->DXT5.
            if (compression == 2) compression = 1;
            if (compression == 3) compression = 5;

            var offset = 24;
            byte[]? palette = null;

            if (compression == 0 && paletteDepth > 0)
            {
                if (offset + 4 > data.Length)
                    return Ps2TexResult.Fail("Truncated palette header");

                var paletteColorCount = (int)BitConverter.ToUInt32(data[offset..]);
                offset += 4;

                var paletteEntryBytes = Math.Max(1, (paletteDepth + 7) / 8);
                var paletteBytes = paletteColorCount * paletteEntryBytes;
                if (offset + paletteBytes > data.Length)
                    return Ps2TexResult.Fail("Truncated palette data");

                palette = data.Slice(offset, paletteBytes).ToArray();
                offset += paletteBytes;
            }

            byte[]? pixels = null;
            var mipW = width;
            var mipH = height;

            for (var mip = 0; mip < mipCount; mip++)
            {
                if (offset + 4 > data.Length)
                    return Ps2TexResult.Fail($"Truncated mip header at mip {mip}");

                int dataSize;
                if (compression != 0)
                {
                    dataSize = (int)BitConverter.ToUInt32(data[offset..]);
                    offset += 4;
                }
                else
                {
                    var bytesPerLine = (int)BitConverter.ToUInt16(data[offset..]);
                    var numLines = (int)BitConverter.ToUInt16(data[(offset + 2)..]);
                    dataSize = bytesPerLine * numLines;
                    offset += 4;
                }

                if (offset + dataSize > data.Length)
                    return Ps2TexResult.Fail($"Truncated mip data at mip {mip}");

                if (mip == 0)
                {
                    pixels = DecodeMip(data.Slice(offset, dataSize), mipW, mipH, texelDepth, compression, palette);
                    if (pixels == null)
                        return Ps2TexResult.Fail("Failed to decode THAW IMG mip 0");

                    // THAW PC textures are stored bottom-up, matching .tex.wpc.
                    FlipVertical(pixels, mipW, mipH);
                }

                offset += dataSize;
                mipW = Math.Max(1, mipW >> 1);
                mipH = Math.Max(1, mipH >> 1);
            }

            return new Ps2TexResult([new Ps2Texture(checksum, width, height, 0, 0, pixels)]);
        }
        catch (Exception ex)
        {
            return Ps2TexResult.Fail(ex.Message);
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
                if (idx >= paletteEntries)
                    continue;

                var pi = idx * 4;
                var oi = i * 4;
                output[oi] = palette[pi];
                output[oi + 1] = palette[pi + 1];
                output[oi + 2] = palette[pi + 2];
                output[oi + 3] = palette[pi + 3];
            }
        }
        else if (texelDepth == 4)
        {
            for (var i = 0; i < width * height; i++)
            {
                var byteIdx = i / 2;
                if (byteIdx >= data.Length)
                    break;

                var idx = (i & 1) == 0 ? data[byteIdx] & 0x0F : data[byteIdx] >> 4;
                if (idx >= paletteEntries)
                    continue;

                var pi = idx * 4;
                var oi = i * 4;
                output[oi] = palette[pi];
                output[oi + 1] = palette[pi + 1];
                output[oi + 2] = palette[pi + 2];
                output[oi + 3] = palette[pi + 3];
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
            output[oi] = data[si + 2];
            output[oi + 1] = data[si + 1];
            output[oi + 2] = data[si];
            output[oi + 3] = data[si + 3];
        }

        return output;
    }

    private static void FlipVertical(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        var temp = new byte[rowBytes];

        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var topOffset = top * rowBytes;
            var bottomOffset = bottom * rowBytes;
            Buffer.BlockCopy(pixels, topOffset, temp, 0, rowBytes);
            Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, rowBytes);
            Buffer.BlockCopy(temp, 0, pixels, bottomOffset, rowBytes);
        }
    }
}
