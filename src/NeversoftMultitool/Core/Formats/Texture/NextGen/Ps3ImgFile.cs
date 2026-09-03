using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.NextGen;

/// <summary>
///     Parses the 128-byte PlayStation 3 <c>.img.ps3</c> descriptor and its
///     separate <c>.imv.ps3</c> block-compressed payload.
/// </summary>
/// <remarks>
///     Unlike Xenon IMG, PS3 keeps the descriptor and pixels in separate files.
///     The IMV bytes are ordinary little-endian BC1/BC3 blocks: no byte swap,
///     GPU tiling, or row flip is applied. The layout is pinned over 2,853
///     descriptors from Project 8 and Proving Ground; every descriptor declares
///     exactly one level and repeats its dimensions twice.
/// </remarks>
public static class Ps3ImgFile
{
    public const uint Magic = 0x0C301100;
    public const uint AlternateMagic = 0x0C300100;
    public const int DescriptorSize = 128;

    private const byte GcmFormatDxt1 = 0x86;
    private const byte GcmFormatDxt5 = 0x88;
    private const byte GcmLinearFlag = 0x20;
    private const int MaximumDimension = 16384;

    /// <summary>True only for a complete, internally consistent PS3 IMG descriptor.</summary>
    public static bool IsPs3Img(ReadOnlySpan<byte> data)
    {
        return TryInspect(data, out _, out _);
    }

    /// <summary>Parses a loose descriptor and resolves its payload from disk.</summary>
    public static Ps2TexResult Parse(string descriptorPath)
    {
        try
        {
            var descriptor = File.ReadAllBytes(descriptorPath);
            if (!TryInspect(descriptor, out var info, out var error))
                return Ps2TexResult.Fail(error);

            var resolution = Ps3ImgPayloadLocator.Resolve(descriptorPath, info.PayloadSize);
            if (!resolution.Found)
                return Ps2TexResult.Fail(resolution.Message);

            return Decode(info, resolution.Bytes!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    /// <summary>
    ///     Parses a descriptor from a filesystem or archive-backed source and
    ///     resolves the matching IMV through the same source abstraction.
    /// </summary>
    public static Ps2TexResult Parse(AssetSource source, byte[] descriptor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!TryInspect(descriptor, out var info, out var error))
            return Ps2TexResult.Fail(error);

        var resolution = Ps3ImgPayloadLocator.Resolve(source, info.PayloadSize);
        return resolution.Found
            ? Decode(info, resolution.Bytes!)
            : Ps2TexResult.Fail(resolution.Message);
    }

    /// <summary>Parses bytes when the caller already resolved the paired IMV.</summary>
    public static Ps2TexResult Parse(byte[] descriptor, byte[]? payload)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!TryInspect(descriptor, out var info, out var error))
            return Ps2TexResult.Fail(error);
        if (payload == null)
            return Ps2TexResult.Fail("PS3 IMG pixel payload (.imv.ps3) was not found");

        return Decode(info, payload);
    }

    internal static bool TryInspect(
        ReadOnlySpan<byte> data,
        out Ps3ImgInfo info,
        out string error)
    {
        info = default;
        error = "";

        if (data.Length != DescriptorSize)
        {
            error = $"PS3 IMG descriptor must be exactly {DescriptorSize} bytes (got {data.Length})";
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (magic is not (Magic or AlternateMagic))
        {
            error = $"Not a PS3 IMG descriptor (magic 0x{magic:X8})";
            return false;
        }

        var signature = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (signature is not (0x55434F44 or 0)) // "UCOD" in Project 8; zero in Proving Ground.
        {
            error = $"Unexpected PS3 IMG signature 0x{signature:X8} at +0x04";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension)
        {
            error = $"Implausible PS3 IMG dimensions {width}x{height}";
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(data[0x0C..]) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x0E..]) != width
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x10..]) != height
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x12..]) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x38..]) != width
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x3A..]) != height
            || BinaryPrimitives.ReadUInt16BigEndian(data[0x3C..]) != 1)
        {
            error = "PS3 IMG repeated dimensions/depth fields disagree";
            return false;
        }

        var levels = data[0x14];
        var texelDepth = data[0x15];
        var dxtVersion = data[0x16];
        if (levels != 1)
        {
            error = $"Unsupported PS3 IMG level count {levels} (expected 1)";
            return false;
        }

        if (data[0x17] != 0x18
            || BinaryPrimitives.ReadUInt64BigEndian(data[0x18..]) != 0
            || BinaryPrimitives.ReadUInt32BigEndian(data[0x24..]) != 1
            || BinaryPrimitives.ReadUInt32BigEndian(data[0x28..]) != 0x30
            || BinaryPrimitives.ReadUInt32BigEndian(data[0x2C..]) != 0
            || data[0x31] != 1
            || data[0x32] != 2
            || data[0x33] != 0
            || BinaryPrimitives.ReadUInt32BigEndian(data[0x34..]) != 0x0000AAE4
            || data[0x3E] != 0
            || data[0x3F] != 0
            || !IsAllZero(data[0x44..]))
        {
            error = "PS3 IMG fixed descriptor fields are inconsistent";
            return false;
        }

        var gcmFormat = data[0x30];
        var baseFormat = (byte)(gcmFormat & ~GcmLinearFlag);
        var blockBytes = baseFormat switch
        {
            GcmFormatDxt1 => 8,
            GcmFormatDxt5 => 16,
            _ => 0
        };
        if (blockBytes == 0)
        {
            error = $"Unsupported PS3 IMG GCM format 0x{gcmFormat:X2}";
            return false;
        }

        var formatMetadataMatches = baseFormat switch
        {
            GcmFormatDxt1 => texelDepth == 4 && dxtVersion is 1 or 2,
            GcmFormatDxt5 => texelDepth == 8 && dxtVersion == 5,
            _ => false
        };
        if (!formatMetadataMatches)
        {
            error =
                $"PS3 IMG {DescribeFormat(gcmFormat)} metadata disagrees " +
                $"(texel depth {texelDepth}, DXT version {dxtVersion})";
            return false;
        }

        var blocksWide = ((long)width + 3) / 4;
        var blocksHigh = ((long)height + 3) / 4;
        var expectedPayloadSize = checked(blocksWide * blocksHigh * blockBytes);
        var declaredPayloadSize = BinaryPrimitives.ReadUInt32BigEndian(data[0x20..]);
        if (expectedPayloadSize > int.MaxValue || declaredPayloadSize != expectedPayloadSize)
        {
            error =
                $"PS3 IMG payload-size mismatch: descriptor declares {declaredPayloadSize}, " +
                $"{width}x{height} {DescribeFormat(gcmFormat)} requires {expectedPayloadSize}";
            return false;
        }

        var rgbaBytes = (long)width * height * 4;
        if (rgbaBytes > Array.MaxLength)
        {
            error = $"PS3 IMG dimensions {width}x{height} exceed the RGBA buffer limit";
            return false;
        }

        info = new Ps3ImgInfo(
            magic,
            width,
            height,
            levels,
            texelDepth,
            dxtVersion,
            gcmFormat,
            (int)expectedPayloadSize);
        return true;
    }

    private static Ps2TexResult Decode(Ps3ImgInfo info, byte[] payload)
    {
        try
        {
            if (payload.Length != info.PayloadSize)
            {
                var condition = payload.Length < info.PayloadSize ? "truncated" : "oversized";
                return Ps2TexResult.Fail(
                    $"PS3 IMG payload is {condition}: expected {info.PayloadSize} bytes, " +
                    $"got {payload.Length}");
            }

            var rgba = info.BaseGcmFormat switch
            {
                GcmFormatDxt1 => DxtDecoder.DecodeDxt1(payload, info.Width, info.Height),
                GcmFormatDxt5 => DxtDecoder.DecodeDxt5(payload, info.Width, info.Height),
                _ => throw new InvalidDataException(
                    $"Unsupported PS3 IMG GCM format 0x{info.GcmFormat:X2}")
            };

            return new Ps2TexResult(
            [
                new Ps2Texture(
                    0,
                    info.Width,
                    info.Height,
                    info.GcmFormat,
                    info.DxtVersion,
                    rgba)
            ]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException
                                   or OverflowException)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static string DescribeFormat(byte gcmFormat)
    {
        var suffix = (gcmFormat & GcmLinearFlag) != 0 ? " (linear flag)" : "";
        return (byte)(gcmFormat & ~GcmLinearFlag) switch
        {
            GcmFormatDxt1 => "DXT1" + suffix,
            GcmFormatDxt5 => "DXT5" + suffix,
            _ => $"GCM 0x{gcmFormat:X2}"
        };
    }

    internal readonly record struct Ps3ImgInfo(
        uint DescriptorMagic,
        int Width,
        int Height,
        byte Levels,
        byte TexelDepth,
        byte DxtVersion,
        byte GcmFormat,
        int PayloadSize)
    {
        public byte BaseGcmFormat => (byte)(GcmFormat & ~GcmLinearFlag);
        public bool HasLinearFlag => (GcmFormat & GcmLinearFlag) != 0;
        public string FormatName => DescribeFormat(GcmFormat);
    }
}
