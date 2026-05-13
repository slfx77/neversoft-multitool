using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

public static class NgcTexFile
{
    private const byte ExpectedConstantA = 0x01;
    private const byte ExpectedConstantB = 0x08;
    private const byte SupportedFormatA = 14;
    private const byte SupportedFormatB = 12;
    private const int HeaderSize = 8;
    private const int EntrySize = 32;

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

    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        if (!TryReadHeader(data, out var header, out var error))
        {
            return Ps2TexResult.Fail(error);
        }

        var textures = new List<Ps2Texture>(header.TextureCount);
        for (var index = 0; index < header.TextureCount; index++)
        {
            if (!TryReadEntry(data, header, index, out var entry, out error))
            {
                return Ps2TexResult.Fail(error);
            }

            if (!IsSupportedFormat(entry.FormatA, entry.FormatB))
            {
                return Ps2TexResult.Fail(
                    $"Unsupported NGC texture format ({entry.FormatA},{entry.FormatB}) in entry {index} (checksum 0x{entry.Checksum:X8}).");
            }

            var dataEnd = checked(entry.DataOffset + entry.DataSize);
            if (dataEnd > data.Length)
            {
                return Ps2TexResult.Fail(
                    $"Texture data for entry {index} extends past end of file (offset {entry.DataOffset}, size {entry.DataSize}).");
            }

            byte[] pixels;
            try
            {
                pixels = NgcTexCmprDecoder.DecodeToRgba(
                    data.Slice(entry.DataOffset, entry.DataSize),
                    entry.Width,
                    entry.Height);
            }
            catch (Exception ex)
            {
                return Ps2TexResult.Fail(
                    $"Failed to decode NGC texture entry {index} (checksum 0x{entry.Checksum:X8}): {ex.Message}");
            }

            textures.Add(new Ps2Texture(
                entry.Checksum,
                entry.Width,
                entry.Height,
                0,
                0,
                pixels,
                QbKey.QbKey.TryResolve(entry.Checksum)));
        }

        return new Ps2TexResult(textures);
    }

    public static int SaveAllAsPng(Ps2TexResult result, string outputDir, string stem)
    {
        if (!result.Success)
        {
            return 0;
        }

        var count = 0;
        foreach (var texture in result.Textures)
        {
            if (texture.Pixels == null)
            {
                continue;
            }

            var name = texture.Name ?? QbKey.QbKey.TryResolve(texture.Checksum) ?? $"{texture.Checksum:X8}";
            var path = Path.Combine(outputDir, stem, $"{name}.png");
            ImageWriter.WritePng(path, texture.Width, texture.Height, texture.Pixels);
            count++;
        }

        return count;
    }

    internal static bool TryReadHeader(ReadOnlySpan<byte> data, out NgcTexHeader header, out string error)
    {
        header = default;

        if (data.Length < HeaderSize)
        {
            error = "File too small";
            return false;
        }

        var constantA = data[0];
        var constantB = data[1];
        var textureCount = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var metadataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

        if (constantA != ExpectedConstantA || constantB != ExpectedConstantB)
        {
            error = $"Unsupported NGC TEX header ({constantA},{constantB}).";
            return false;
        }

        if (textureCount == 0)
        {
            error = "NGC TEX has no textures.";
            return false;
        }

        if (metadataOffset < HeaderSize || metadataOffset > data.Length - EntrySize)
        {
            error = $"Invalid NGC TEX metadata offset {metadataOffset}.";
            return false;
        }

        var requiredMetadataSize = checked((int)metadataOffset + textureCount * EntrySize);
        if (requiredMetadataSize > data.Length)
        {
            error = "NGC TEX metadata table is truncated.";
            return false;
        }

        header = new NgcTexHeader(textureCount, metadataOffset);
        error = string.Empty;
        return true;
    }

    internal static bool TryReadEntry(
        ReadOnlySpan<byte> data,
        NgcTexHeader header,
        int index,
        out NgcTexEntry entry,
        out string error)
    {
        entry = default;

        if (index < 0 || index >= header.TextureCount)
        {
            error = $"Entry index {index} is out of range.";
            return false;
        }

        var offset = checked((int)header.MetadataOffset + index * EntrySize);
        if (offset > data.Length - EntrySize)
        {
            error = $"NGC TEX entry {index} is truncated.";
            return false;
        }

        var span = data.Slice(offset, EntrySize);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(span);
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        var width = 1 << span[10];
        var height = 1 << span[11];
        var formatA = span[13];
        var formatB = span[14];
        var dataSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[16..]));
        var dataOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[20..]));

        if (width <= 0 || height <= 0)
        {
            error = $"NGC TEX entry {index} has invalid dimensions {width}x{height}.";
            return false;
        }

        if (dataSize <= 0)
        {
            error = $"NGC TEX entry {index} has invalid data size {dataSize}.";
            return false;
        }

        if (dataOffset < HeaderSize || dataOffset > data.Length - dataSize)
        {
            error = $"NGC TEX entry {index} has invalid data range ({dataOffset}, {dataSize}).";
            return false;
        }

        entry = new NgcTexEntry(magic, checksum, width, height, formatA, formatB, dataSize, dataOffset);
        error = string.Empty;
        return true;
    }

    internal static bool HasSupportedFormatsOnly(ReadOnlySpan<byte> data, out string error)
    {
        if (!TryReadHeader(data, out var header, out error))
        {
            return false;
        }

        for (var index = 0; index < header.TextureCount; index++)
        {
            if (!TryReadEntry(data, header, index, out var entry, out error))
            {
                return false;
            }

            if (!IsSupportedFormat(entry.FormatA, entry.FormatB))
            {
                error = $"Unsupported NGC texture format ({entry.FormatA},{entry.FormatB}) in entry {index}.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSupportedFormat(byte formatA, byte formatB)
    {
        return formatA == SupportedFormatA && formatB == SupportedFormatB;
    }
}
