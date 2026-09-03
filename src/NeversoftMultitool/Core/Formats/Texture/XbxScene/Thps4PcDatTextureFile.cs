using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Texture.XbxScene;

/// <summary>
///     THPS4's Aspyr PC port removed the separator before the asset kind:
///     <c>alctex.dat</c> is the same version-1 dictionary otherwise stored as
///     <c>alc.tex.wpc</c>. The generic <c>.dat</c> extension is not sufficient
///     evidence, so every public route uses this name predicate AND a complete,
///     exactly-consumed Xbox-TEX parse.
/// </summary>
public static class Thps4PcDatTextureFile
{
    public const string Suffix = "tex.dat";

    /// <summary>
    ///     The delimiter-free THPS4 PC spelling. Deliberately excludes
    ///     <c>name.tex.dat</c>, which is the unrelated next-gen FACECAA7 family.
    /// </summary>
    public static bool IsCandidateFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Length > Suffix.Length
               && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".tex.dat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parses only a structurally complete dictionary whose last mip ends at
    ///     EOF and whose level-zero image for every record is decodable RGBA32.
    ///     The one authored-empty corpus dictionary is accepted only as the exact
    ///     eight-byte version/count header.
    /// </summary>
    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        if (!TryValidateExactContainer(data, out var error))
            return Ps2TexResult.Fail(error);

        var parsed = XbxTexFile.Parse(data);
        if (!parsed.Success)
            return parsed;

        for (var i = 0; i < parsed.Textures.Count; i++)
        {
            var texture = parsed.Textures[i];
            var expectedLength = (long)texture.Width * texture.Height * 4;
            if (texture.Pixels == null)
            {
                return Ps2TexResult.Fail(
                    $"THPS4 PC TEX texture {i} uses an unsupported pixel format");
            }

            if (texture.Pixels.LongLength != expectedLength)
            {
                return Ps2TexResult.Fail(
                    $"THPS4 PC TEX texture {i} decoded {texture.Pixels.LongLength} RGBA bytes; " +
                    $"expected {expectedLength}");
            }
        }

        return parsed;
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

    private static bool TryValidateExactContainer(ReadOnlySpan<byte> data, out string error)
    {
        error = "";
        if (data.Length < 8)
        {
            error = "File too small for a THPS4 PC TEX header";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != 1)
        {
            error = $"Unsupported THPS4 PC TEX version {version} (expected 1)";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        // Every nonempty record needs a 32-byte header and at least one four-byte
        // mip-size word. This also bounds the loop before converting count to int.
        if (count > (uint)((data.Length - 8) / 36))
        {
            error = $"THPS4 PC TEX declares {count} textures outside the file bounds";
            return false;
        }

        var offset = 8L;
        for (var textureIndex = 0u; textureIndex < count; textureIndex++)
        {
            if (offset > data.Length - 32L)
            {
                error = $"Truncated THPS4 PC TEX header at texture {textureIndex}";
                return false;
            }

            var header = data.Slice((int)offset, 32);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var height = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var levels = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var texelDepth = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var dxtVersion = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var paletteSize = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
            if (width is 0 or > 4096 || height is 0 or > 4096)
            {
                error = $"THPS4 PC TEX texture {textureIndex} has invalid dimensions {width}x{height}";
                return false;
            }

            if (levels is 0 or > 32)
            {
                error = $"THPS4 PC TEX texture {textureIndex} has invalid mip level count {levels}";
                return false;
            }

            offset += 32;
            if (paletteSize > data.Length - offset)
            {
                error = $"Truncated THPS4 PC TEX palette at texture {textureIndex}";
                return false;
            }

            var paletteOffset = offset;
            offset += paletteSize;
            var palette = data.Slice((int)paletteOffset, (int)paletteSize);
            for (var mip = 0u; mip < levels; mip++)
            {
                if (offset > data.Length - 4L)
                {
                    error = $"Truncated THPS4 PC TEX mip header at texture {textureIndex}, mip {mip}";
                    return false;
                }

                var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)offset, 4));
                offset += 4;
                if (dataSize > data.Length - offset)
                {
                    error = $"Truncated THPS4 PC TEX mip data at texture {textureIndex}, mip {mip}";
                    return false;
                }

                var mipWidth = Math.Max(1u, width >> checked((int)mip));
                var mipHeight = Math.Max(1u, height >> checked((int)mip));
                if (!TryValidateMip(
                        data.Slice((int)offset, (int)dataSize),
                        mipWidth,
                        mipHeight,
                        texelDepth,
                        dxtVersion,
                        palette,
                        textureIndex,
                        mip,
                        out error))
                {
                    return false;
                }

                offset += dataSize;
            }
        }

        if (offset != data.Length)
        {
            error = $"THPS4 PC TEX has {data.Length - offset} trailing bytes after the dictionary";
            return false;
        }

        return true;
    }

    private static bool TryValidateMip(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height,
        uint texelDepth,
        uint dxtVersion,
        ReadOnlySpan<byte> palette,
        uint textureIndex,
        uint mip,
        out string error)
    {
        error = "";
        var pixelCount = (long)width * height;
        long requiredBytes;
        switch (dxtVersion)
        {
            case 1:
            case 2:
                requiredBytes = ((width + 3L) / 4) * ((height + 3L) / 4) * 8;
                break;
            case 5:
                requiredBytes = ((width + 3L) / 4) * ((height + 3L) / 4) * 16;
                break;
            case 0 when palette.Length > 0 && texelDepth is 4 or 8:
            {
                if ((palette.Length & 3) != 0)
                {
                    error = $"THPS4 PC TEX texture {textureIndex} has a partial BGRA palette";
                    return false;
                }

                requiredBytes = texelDepth == 8 ? pixelCount : (pixelCount + 1) / 2;
                break;
            }
            case 0 when palette.Length == 0 && texelDepth == 32:
                requiredBytes = pixelCount * 4;
                break;
            case 0 when palette.Length == 0 && texelDepth == 16:
                requiredBytes = pixelCount * 2;
                break;
            default:
                error = $"THPS4 PC TEX texture {textureIndex} uses unsupported " +
                        $"depth/DXT/palette ({texelDepth}/{dxtVersion}/{palette.Length})";
                return false;
        }

        if ((long)pixels.Length != requiredBytes)
        {
            error = $"THPS4 PC TEX texture {textureIndex}, mip {mip} has " +
                    $"{pixels.Length} stored bytes; expected exactly {requiredBytes} " +
                    $"for {width}x{height}";
            return false;
        }

        if (dxtVersion == 0 && palette.Length > 0)
        {
            var paletteEntries = palette.Length / 4;
            for (var pixelIndex = 0L; pixelIndex < pixelCount; pixelIndex++)
            {
                var value = pixels[(int)(texelDepth == 8 ? pixelIndex : pixelIndex / 2)];
                var paletteIndex = texelDepth == 8
                    ? value
                    : (pixelIndex & 1) == 0
                        ? value & 0x0F
                        : value >> 4;
                if (paletteIndex >= paletteEntries)
                {
                    error = $"THPS4 PC TEX texture {textureIndex}, mip {mip} references " +
                            $"palette index {paletteIndex}, but only {paletteEntries} entries exist";
                    return false;
                }
            }
        }

        return true;
    }
}
