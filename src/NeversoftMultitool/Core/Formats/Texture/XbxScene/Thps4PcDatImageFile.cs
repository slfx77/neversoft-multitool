using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.XbxScene;

/// <summary>
///     Parses the delimiter-free <c>*img.dat</c> images from Aspyr's THPS4
///     Windows port. The 32-byte header retains THPS4's exponent/logical-size
///     split, while the payload uses the Xbox P8 Morton layout or tight BGRA32.
/// </summary>
public static class Thps4PcDatImageFile
{
    public const string Suffix = "img.dat";

    private const uint Version = 2;
    private const uint RequiredChecksum = 0x00410230;
    private const uint Bgra32Format = 0;
    private const uint P8Format = 0x13;
    private const int HeaderSize = 32;
    private const int MaximumDimensionExponent = 12;
    private const int MaximumDimension = 1 << MaximumDimensionExponent;

    /// <summary>
    ///     Returns true only for Aspyr's delimiter-free spelling, such as
    ///     <c>blackimg.dat</c>. A conventional <c>black.img.dat</c> is a
    ///     different filename family and is deliberately excluded.
    /// </summary>
    public static bool IsCandidateFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Length > Suffix.Length
               && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".img.dat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parses one image and requires its palette and pixel surface to end
    ///     exactly at EOF. Indexed records use one stored byte per texel over a
    ///     power-of-two Morton surface; BGRA32 records are tightly packed at the
    ///     logical dimensions.
    /// </summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return Ps2TexResult.Fail("File too small for a THPS4 PC IMG header");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != Version)
            return Ps2TexResult.Fail($"Unsupported THPS4 PC IMG version {version} (expected {Version})");

        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (checksum != RequiredChecksum)
        {
            return Ps2TexResult.Fail(
                $"Invalid THPS4 PC IMG checksum 0x{checksum:X8} (expected 0x{RequiredChecksum:X8})");
        }

        var widthExponent = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var heightExponent = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (widthExponent > MaximumDimensionExponent || heightExponent > MaximumDimensionExponent)
        {
            return Ps2TexResult.Fail(
                $"Invalid THPS4 PC IMG surface exponents {widthExponent}x{heightExponent}");
        }

        var format = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        var paletteSize = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        if (width is 0 or > MaximumDimension || height is 0 or > MaximumDimension)
            return Ps2TexResult.Fail($"Invalid THPS4 PC IMG dimensions {width}x{height}");

        byte[] pixels;
        switch (format)
        {
            case P8Format:
            {
                if (paletteSize is not (16 * 4) and not (256 * 4))
                {
                    return Ps2TexResult.Fail(
                        $"Invalid THPS4 PC IMG P8 palette size {paletteSize} (expected 64 or 1024)");
                }

                var surfaceWidth = 1 << (int)widthExponent;
                var surfaceHeight = 1 << (int)heightExponent;
                if (width > surfaceWidth || height > surfaceHeight
                    || (widthExponent > 0 && width <= surfaceWidth / 2)
                    || (heightExponent > 0 && height <= surfaceHeight / 2))
                {
                    return Ps2TexResult.Fail(
                        $"THPS4 PC IMG logical dimensions {width}x{height} do not match " +
                        $"the {surfaceWidth}x{surfaceHeight} P8 surface");
                }

                var payloadSize = (long)surfaceWidth * surfaceHeight;
                if (!HasExactLength(data.Length, paletteSize, payloadSize, out var lengthError))
                    return Ps2TexResult.Fail(lengthError);

                var palette = data.Slice(HeaderSize, (int)paletteSize);
                var indices = data.Slice(HeaderSize + (int)paletteSize, (int)payloadSize);
                var paletteEntries = palette.Length / 4;
                for (var i = 0; i < indices.Length; i++)
                {
                    if (indices[i] >= paletteEntries)
                    {
                        return Ps2TexResult.Fail(
                            $"THPS4 PC IMG pixel {i} references palette index {indices[i]}, " +
                            $"but only {paletteEntries} entries exist");
                    }
                }

                pixels = DecodePalettedBottomUp(
                    indices,
                    width,
                    height,
                    surfaceWidth,
                    surfaceHeight,
                    palette);
                break;
            }
            case Bgra32Format:
            {
                if (paletteSize != 0)
                    return Ps2TexResult.Fail("THPS4 PC IMG BGRA32 records cannot carry a palette");

                if (!MatchesFloorExponent(width, widthExponent)
                    || !MatchesFloorExponent(height, heightExponent))
                {
                    return Ps2TexResult.Fail(
                        $"THPS4 PC IMG BGRA32 dimensions {width}x{height} do not match " +
                        $"the stored exponents {widthExponent}x{heightExponent}");
                }

                var payloadSize = (long)width * height * 4;
                if (!HasExactLength(data.Length, paletteSize, payloadSize, out var lengthError))
                    return Ps2TexResult.Fail(lengthError);

                pixels = DecodeBgra32BottomUp(data[HeaderSize..], width, height);
                break;
            }
            default:
                return Ps2TexResult.Fail($"Unsupported THPS4 PC IMG pixel format 0x{format:X8}");
        }

        return new Ps2TexResult([new Ps2Texture(checksum, width, height, format, 0, pixels)]);
    }

    public static Ps2TexResult Parse(string filePath)
    {
        try
        {
            return Parse(File.ReadAllBytes(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ps2TexResult.Fail(ex.Message);
        }
    }

    private static bool HasExactLength(
        int actualLength,
        uint paletteSize,
        long payloadSize,
        out string error)
    {
        var expectedLength = HeaderSize + (long)paletteSize + payloadSize;
        if (actualLength == expectedLength)
        {
            error = "";
            return true;
        }

        error = actualLength < expectedLength
            ? $"Truncated THPS4 PC IMG payload: expected {expectedLength} bytes, found {actualLength}"
            : $"THPS4 PC IMG has {actualLength - expectedLength} trailing bytes after the pixel surface";
        return false;
    }

    private static bool MatchesFloorExponent(ushort dimension, uint exponent)
    {
        var floor = 1 << (int)exponent;
        return dimension >= floor
               && (exponent == MaximumDimensionExponent || dimension < floor * 2);
    }

    /// <summary>
    ///     Sprite rows occupy the bottom of a padded Morton surface and are
    ///     stored bottom-up. This is independently visible in the matching
    ///     THPS4 PS2 images: selecting the final logical-height rows in reverse
    ///     order reproduces their image orientation and texels.
    /// </summary>
    private static byte[] DecodePalettedBottomUp(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        int surfaceWidth,
        int surfaceHeight,
        ReadOnlySpan<byte> palette)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var storedY = surfaceHeight - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var paletteIndex = data[MortonIndex(x, storedY, surfaceWidth, surfaceHeight)];
                var sourceOffset = paletteIndex * 4;
                var outputOffset = (y * width + x) * 4;
                output[outputOffset] = palette[sourceOffset + 2];
                output[outputOffset + 1] = palette[sourceOffset + 1];
                output[outputOffset + 2] = palette[sourceOffset];
                output[outputOffset + 3] = palette[sourceOffset + 3];
            }
        }

        return output;
    }

    private static byte[] DecodeBgra32BottomUp(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRowOffset = (height - 1 - y) * width * 4;
            var outputRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = sourceRowOffset + x * 4;
                var outputOffset = outputRowOffset + x * 4;
                output[outputOffset] = data[sourceOffset + 2];
                output[outputOffset + 1] = data[sourceOffset + 1];
                output[outputOffset + 2] = data[sourceOffset];
                output[outputOffset + 3] = data[sourceOffset + 3];
            }
        }

        return output;
    }

    private static int MortonIndex(int x, int y, int width, int height)
    {
        var bit = 1;
        var xMask = 0;
        var yMask = 0;
        while (width > 1 || height > 1)
        {
            if (width > 1)
            {
                xMask |= bit;
                bit <<= 1;
                width >>= 1;
            }

            if (height > 1)
            {
                yMask |= bit;
                bit <<= 1;
                height >>= 1;
            }
        }

        return SpreadBits(x, xMask) | SpreadBits(y, yMask);
    }

    private static int SpreadBits(int value, int mask)
    {
        var result = 0;
        var valueBit = 1;
        for (var bit = 1; bit != 0 && mask != 0; bit <<= 1)
        {
            if ((mask & bit) == 0)
                continue;

            if ((value & valueBit) != 0)
                result |= bit;
            valueBit <<= 1;
        }

        return result;
    }
}
