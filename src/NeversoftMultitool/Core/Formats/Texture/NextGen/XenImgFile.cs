using System.Buffers.Binary;
using System.IO.Compression;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Parses the Xbox 360 single-image <c>.img.xen</c> format used by THAW,
///     Project 8 and Proving Ground.
/// </summary>
/// <remarks>
///     The two games' descriptors are not interchangeable: THAW's 32-byte
///     descriptor is followed by its fetch constant at +0x30, while P8/PG use
///     a 40-byte descriptor and a fetch constant at +0x44. In both variants
///     level zero starts at file offset 4096. A complete corpus sweep found
///     13,712 files: 1,034 raw-DEFLATE wrapped, 601 with more than one mip and
///     14 DXN/BC5 surfaces. Only level zero is exposed, matching loose IMG
///     semantics and avoiding the per-level page layout hidden by the declared
///     aggregate size in multi-mip files.
/// </remarks>
public static class XenImgFile
{
    public const uint ThawMagic = 0x08200100;
    public const uint Project8Magic = 0x0A281100;

    internal const int LevelZeroOffset = 4096;

    private const int ThawFetchOffset = 0x30;
    private const int Project8FetchOffset = 0x44;
    private const int MaximumInflatedBytes = 512 * 1024 * 1024;
    private const int MaximumDimension = 16384;

    private const byte Dxt1DescriptorFormat = 1;
    private const byte Dxt3DescriptorFormat = 2;
    private const byte Dxt5DescriptorFormat = 5;
    private const byte DxnDescriptorFormat = 6;

    private const byte Dxt1FetchFormat = 0x12;
    private const byte Dxt3FetchFormat = 0x13;
    private const byte Dxt5FetchFormat = 0x14;
    private const byte DxnFetchFormat = 0x31;

    /// <summary>
    ///     Returns true for either supported descriptor variant, including a
    ///     raw-DEFLATE-wrapped file.
    /// </summary>
    public static bool IsXenImg(ReadOnlySpan<byte> data)
    {
        return TryInspect(data, out _, out _);
    }

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

    public static Ps2TexResult Parse(byte[] data)
    {
        try
        {
            if (!TryPrepare(data, out var body, out var info, out var error))
                return Ps2TexResult.Fail(error);

            var blockBytes = info.DescriptorFormat switch
            {
                Dxt1DescriptorFormat => 8,
                Dxt3DescriptorFormat or Dxt5DescriptorFormat or DxnDescriptorFormat => 16,
                _ => 0
            };
            if (blockBytes == 0)
            {
                return Ps2TexResult.Fail(
                    $"Unsupported Xenon IMG descriptor format {info.DescriptorFormat} " +
                    $"(fetch format 0x{info.FetchFormat:X2})");
            }

            if (!DescriptorMatchesFetch(info.DescriptorFormat, info.FetchFormat))
            {
                return Ps2TexResult.Fail(
                    $"Xenon IMG descriptor format {info.DescriptorFormat} disagrees " +
                    $"with fetch format 0x{info.FetchFormat:X2}");
            }

            if (info.EndianMode != 1)
            {
                return Ps2TexResult.Fail(
                    $"Unsupported Xenon IMG endian mode {info.EndianMode} (expected 16-bit swap)");
            }

            var blocksWide = (info.Width + 3) / 4;
            var blocksHigh = (info.Height + 3) / 4;
            var requiredBytes = checked(blocksWide * blocksHigh * blockBytes);
            var availableBytes = body.Length - LevelZeroOffset;
            var declaredBytes = info.DeclaredDataSize > int.MaxValue
                ? int.MaxValue
                : (int)info.DeclaredDataSize;
            var payloadBytes = Math.Min(availableBytes, declaredBytes);
            if (payloadBytes <= 0)
                return Ps2TexResult.Fail("Xenon IMG level-zero payload is missing");

            var source = body.AsSpan(LevelZeroOffset, payloadBytes);
            byte[] blocks;
            if (info.IsTiled)
            {
                if (!TryUntileBlocks(source, blocksWide, blocksHigh, blockBytes,
                        out blocks, out error))
                {
                    return Ps2TexResult.Fail(error);
                }
            }
            else
            {
                if (source.Length < requiredBytes)
                {
                    return Ps2TexResult.Fail(
                        $"Xenon IMG level zero is truncated: needs {requiredBytes} bytes, " +
                        $"has {source.Length}");
                }

                blocks = source[..requiredBytes].ToArray();
            }

            Swap16InPlace(blocks);

            var rgba = info.DescriptorFormat switch
            {
                Dxt1DescriptorFormat => DxtDecoder.DecodeDxt1(blocks, info.Width, info.Height),
                Dxt3DescriptorFormat => DxtDecoder.DecodeDxt3(blocks, info.Width, info.Height),
                Dxt5DescriptorFormat => DxtDecoder.DecodeDxt5(blocks, info.Width, info.Height),
                DxnDescriptorFormat => DxtDecoder.DecodeBc5(blocks, info.Width, info.Height),
                _ => throw new InvalidOperationException("Validated Xenon IMG format changed")
            };

            // THAW loose IMG rows are bottom-up; P8/PG changed the convention to top-down.
            if (info.Magic == ThawMagic)
                rgba = FlipRows(rgba, info.Width, info.Height);

            return new Ps2TexResult(
            [
                new Ps2Texture(0, info.Width, info.Height,
                    info.DescriptorFormat, info.FetchFormat, rgba)
            ]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    internal static bool TryInspect(
        ReadOnlySpan<byte> data,
        out XenImgInfo info,
        out string error)
    {
        return TryPrepare(data.ToArray(), out _, out info, out error);
    }

    private static bool TryPrepare(
        byte[] data,
        out byte[] body,
        out XenImgInfo info,
        out string error)
    {
        info = default;
        if (!TryUnwrap(data, out body, out var wasDeflated, out error))
            return false;

        var magic = BinaryPrimitives.ReadUInt32BigEndian(body);
        var fetchOffset = magic == ThawMagic ? ThawFetchOffset : Project8FetchOffset;
        if (body.Length < fetchOffset + 8)
        {
            error = $"Xenon IMG descriptor is truncated before fetch constant +0x{fetchOffset:X}";
            return false;
        }

        if (body.Length <= LevelZeroOffset)
        {
            error = "Xenon IMG is truncated before its level-zero payload at +0x1000";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(8));
        var height = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(10));
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension)
        {
            error = $"Implausible Xenon IMG dimensions {width}x{height}";
            return false;
        }

        var pixelBytes = (long)width * height * 4;
        if (pixelBytes > int.MaxValue)
        {
            error = $"Xenon IMG dimensions {width}x{height} exceed the RGBA buffer limit";
            return false;
        }

        byte mipLevels;
        byte depth;
        byte descriptorFormat;
        uint declaredDataSize;
        if (magic == ThawMagic)
        {
            mipLevels = body[0x10];
            depth = body[0x11];
            descriptorFormat = body[0x13];
            declaredDataSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x18));
        }
        else
        {
            mipLevels = body[0x14];
            depth = body[0x15];
            descriptorFormat = body[0x16];
            declaredDataSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0x20));
        }

        if (mipLevels == 0)
        {
            error = "Xenon IMG declares zero mip levels";
            return false;
        }

        if (declaredDataSize == 0)
        {
            error = "Xenon IMG declares an empty payload";
            return false;
        }

        var fetch0 = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(fetchOffset));
        var fetch1 = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(fetchOffset + 4));
        var fetchFormat = (byte)(fetch1 & 0x3F);
        var endianMode = (byte)((fetch1 >> 6) & 3);

        info = new XenImgInfo(
            magic,
            width,
            height,
            mipLevels,
            depth,
            descriptorFormat,
            declaredDataSize,
            fetchFormat,
            endianMode,
            (fetch0 & 0x80000000) != 0,
            wasDeflated);
        error = "";
        return true;
    }

    private static bool TryUnwrap(
        byte[] data,
        out byte[] body,
        out bool wasDeflated,
        out string error)
    {
        body = data;
        wasDeflated = false;
        error = "";

        if (HasMagic(data))
            return true;

        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = deflate.Read(buffer);
                if (count == 0)
                    break;
                if (output.Length + count > MaximumInflatedBytes)
                {
                    error = $"Raw-DEFLATE Xenon IMG exceeds {MaximumInflatedBytes} bytes";
                    return false;
                }

                output.Write(buffer, 0, count);
            }

            var inflated = output.ToArray();
            if (!HasMagic(inflated))
            {
                error = "Raw-DEFLATE data did not decode to a Xenon IMG descriptor";
                return false;
            }

            body = inflated;
            wasDeflated = true;
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Not a Xenon IMG descriptor or raw-DEFLATE-wrapped Xenon IMG";
            return false;
        }
    }

    private static bool HasMagic(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        return magic is ThawMagic or Project8Magic;
    }

    private static bool DescriptorMatchesFetch(byte descriptorFormat, byte fetchFormat)
    {
        return (descriptorFormat, fetchFormat) switch
        {
            (Dxt1DescriptorFormat, Dxt1FetchFormat) => true,
            (Dxt3DescriptorFormat, Dxt3FetchFormat) => true,
            (Dxt5DescriptorFormat, Dxt5FetchFormat) => true,
            (DxnDescriptorFormat, DxnFetchFormat) => true,
            _ => false
        };
    }

    private static void Swap16InPlace(Span<byte> data)
    {
        for (var offset = 0; offset + 1 < data.Length; offset += 2)
            (data[offset], data[offset + 1]) = (data[offset + 1], data[offset]);
    }

    private static bool TryUntileBlocks(
        ReadOnlySpan<byte> source,
        int blocksWide,
        int blocksHigh,
        int blockBytes,
        out byte[] output,
        out string error)
    {
        output = new byte[checked(blocksWide * blocksHigh * blockBytes)];
        var alignedWidth = Align(blocksWide, 32);

        for (var y = 0; y < blocksHigh; y++)
        {
            for (var x = 0; x < blocksWide; x++)
            {
                var sourceBlock = GetTiledOffset(x, y, alignedWidth, blockBytes);
                var sourceOffset = checked(sourceBlock * blockBytes);
                if (sourceOffset < 0 || sourceOffset > source.Length - blockBytes)
                {
                    error =
                        $"Xenon IMG tiled level zero is truncated at block ({x},{y}): " +
                        $"offset {sourceOffset}, payload {source.Length}";
                    output = [];
                    return false;
                }

                var destinationOffset = (y * blocksWide + x) * blockBytes;
                source.Slice(sourceOffset, blockBytes)
                    .CopyTo(output.AsSpan(destinationOffset, blockBytes));
            }
        }

        error = "";
        return true;
    }

    /// <summary>
    ///     XGAddress2DTiledOffset, expressed in compressed-block units. Xbox 360
    ///     DXT surfaces tile 4x4 blocks, not decoded pixels.
    /// </summary>
    private static int GetTiledOffset(int x, int y, int width, int texelPitch)
    {
        var alignedWidth = Align(width, 32);
        var log2BytesPerUnit = Log2BytesPerUnit(texelPitch);
        var macro = ((x >> 5) + (y >> 5) * (alignedWidth >> 5)) << (log2BytesPerUnit + 7);
        var micro = ((x & 7) + ((y & 6) << 2)) << log2BytesPerUnit;
        var offset = macro + (micro & ~15) * 2 + (micro & 15)
                     + ((y & 8) << (3 + log2BytesPerUnit)) + ((y & 1) << 4);
        return ((offset & ~511) * 8 + (offset & 448) * 4 + (offset & 63)
                + ((y & 16) << 7) + ((((y & 8) >> 2) + (x >> 3)) & 3) * 64)
               >> log2BytesPerUnit;
    }

    private static int Log2BytesPerUnit(int bytesPerUnit)
    {
        return (bytesPerUnit >> 2) + ((bytesPerUnit >> 1) >> (bytesPerUnit >> 2));
    }

    private static int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    private static byte[] FlipRows(byte[] rgba, int width, int height)
    {
        var rowBytes = checked(width * 4);
        var output = new byte[rgba.Length];
        for (var y = 0; y < height; y++)
        {
            rgba.AsSpan(y * rowBytes, rowBytes)
                .CopyTo(output.AsSpan((height - 1 - y) * rowBytes, rowBytes));
        }

        return output;
    }

    internal readonly record struct XenImgInfo(
        uint Magic,
        int Width,
        int Height,
        byte MipLevels,
        byte Depth,
        byte DescriptorFormat,
        uint DeclaredDataSize,
        byte FetchFormat,
        byte EndianMode,
        bool IsTiled,
        bool WasDeflated)
    {
        public bool IsDxn =>
            DescriptorFormat == DxnDescriptorFormat && FetchFormat == DxnFetchFormat;

        public bool IsSupportedFormat =>
            DescriptorMatchesFetch(DescriptorFormat, FetchFormat);

        public string FormatName => DescriptorFormat switch
        {
            Dxt1DescriptorFormat => "DXT1",
            Dxt3DescriptorFormat => "DXT3",
            Dxt5DescriptorFormat => "DXT5",
            DxnDescriptorFormat when FetchFormat == DxnFetchFormat => "DXN/BC5",
            _ => $"format {DescriptorFormat}/0x{FetchFormat:X2}"
        };
    }
}
