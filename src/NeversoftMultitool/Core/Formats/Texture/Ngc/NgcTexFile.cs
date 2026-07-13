using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

public static class NgcTexFile
{
    private const byte ExpectedConstantA = 0x01;
    private const byte ExpectedConstantB = 0x08;
    private const byte GxTfRgba8 = 6;
    private const byte GxTfCmpr = 14;
    private const byte RecordVersion = 4;
    private const byte RecordDepth = 32;
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
        // Bare .img.ngc files are a single 32-byte record with no dictionary header.
        if (IsBareRecord(data))
        {
            if (!TryReadEntryAt(data, 0, 0, out var bareEntry, out var bareError))
                return Ps2TexResult.Fail(bareError);
            return DecodeEntries(data, [bareEntry]);
        }

        if (!TryReadHeader(data, out var header, out var error))
        {
            return Ps2TexResult.Fail(error);
        }

        var entries = new List<NgcTexEntry>(header.TextureCount);
        for (var index = 0; index < header.TextureCount; index++)
        {
            if (!TryReadEntry(data, header, index, out var entry, out error))
            {
                return Ps2TexResult.Fail(error);
            }

            entries.Add(entry);
        }

        return DecodeEntries(data, entries);
    }

    /// <summary>Returns true for a bare .img.ngc record (no dictionary header).</summary>
    public static bool IsBareRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length < EntrySize || data[0] != RecordVersion || data[1] != RecordDepth)
            return false;
        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return dataOffset >= EntrySize && dataOffset <= (uint)data.Length;
    }

    private static Ps2TexResult DecodeEntries(ReadOnlySpan<byte> data, IReadOnlyList<NgcTexEntry> entries)
    {
        var textures = new List<Ps2Texture>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!IsSupportedFormat(entry.FormatA))
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
            var width = entry.Width;
            var height = entry.Height;
            try
            {
                if (entry.FormatA == GxTfCmpr)
                {
                    pixels = NgcTexCmprDecoder.DecodeToRgba(
                        data.Slice(entry.DataOffset, entry.DataSize),
                        width,
                        height);

                    // DXT5-style alpha ships as a second CMPR chain whose green
                    // channel carries the alpha (same trick THUG GC used, see
                    // Sample/thug Gfx/NGC/NX/texture.cpp).
                    if (entry.AlphaOffset >= 0 && entry.AlphaOffset + entry.DataSize <= data.Length)
                    {
                        var alpha = NgcTexCmprDecoder.DecodeToRgba(
                            data.Slice(entry.AlphaOffset, entry.DataSize),
                            width,
                            height);
                        for (var i = 0; i < width * height; i++)
                            pixels[i * 4 + 3] = alpha[i * 4 + 1];
                    }
                }
                else
                {
                    // Format 6 covers two layouts, distinguishable by data size:
                    // C8 indices + trailing 256-entry RGB5A3 palette (CAS icons,
                    // banners) or RGBA8 tiles. Header dimensions are the padded
                    // power of two; the real height falls out of the data size
                    // (512x384 load screens declare 512x512).
                    var c8Rows = entry.DataSize > NgcTexC8Decoder.PaletteBytes &&
                                 (entry.DataSize - NgcTexC8Decoder.PaletteBytes) % width == 0
                        ? (entry.DataSize - NgcTexC8Decoder.PaletteBytes) / width
                        : 0;
                    if (c8Rows > 0 && c8Rows <= height)
                    {
                        height = c8Rows;
                        pixels = NgcTexC8Decoder.DecodeToRgba(
                            data.Slice(entry.DataOffset, entry.DataSize),
                            width,
                            height);
                    }
                    else
                    {
                        var rows = entry.DataSize / (4 * width);
                        if (rows > 0 && rows < height)
                            height = rows;
                        pixels = NgcTexRgba8Decoder.DecodeToRgba(
                            data.Slice(entry.DataOffset, entry.DataSize),
                            width,
                            height);
                    }
                }
            }
            catch (Exception ex)
            {
                return Ps2TexResult.Fail(
                    $"Failed to decode NGC texture entry {index} (checksum 0x{entry.Checksum:X8}): {ex.Message}");
            }

            FlipRows(pixels, width, height);
            textures.Add(new Ps2Texture(
                entry.Checksum,
                width,
                height,
                0,
                0,
                pixels,
                ThawTextureNames.TryResolve(entry.Checksum) ?? QbKey.QbKey.TryResolve(entry.Checksum)));
        }

        return new Ps2TexResult(textures);
    }

    /// <summary>GC textures are stored bottom-up; flip to top-down.</summary>
    private static void FlipRows(byte[] pixels, int width, int height)
    {
        var stride = width * 4;
        var buffer = new byte[stride];
        for (var y = 0; y < height / 2; y++)
        {
            var top = pixels.AsSpan(y * stride, stride);
            var bottom = pixels.AsSpan((height - 1 - y) * stride, stride);
            top.CopyTo(buffer);
            bottom.CopyTo(top);
            buffer.AsSpan().CopyTo(bottom);
        }
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
            // Empty dictionaries ship as 32-byte stubs (e.g. ped_bum_legs03.tex.ngc).
            header = new NgcTexHeader(0, metadataOffset);
            error = string.Empty;
            return true;
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

        return TryReadEntryAt(data, offset, index, out entry, out error);
    }

    private static bool TryReadEntryAt(
        ReadOnlySpan<byte> data,
        int offset,
        int index,
        out NgcTexEntry entry,
        out string error)
    {
        entry = default;
        var span = data.Slice(offset, EntrySize);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(span);
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        var width = 1 << span[10];
        var height = 1 << span[11];
        var formatA = span[13];
        var formatB = span[14];
        var dataSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[16..]));
        var dataOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[20..]));
        var alphaRaw = BinaryPrimitives.ReadUInt32BigEndian(span[24..]);
        var alphaOffset = alphaRaw == 0xFFFFFFFF ? -1 : checked((int)alphaRaw);

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

        entry = new NgcTexEntry(magic, checksum, width, height, formatA, formatB, dataSize, dataOffset, alphaOffset);
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

            if (!IsSupportedFormat(entry.FormatA))
            {
                error = $"Unsupported NGC texture format ({entry.FormatA},{entry.FormatB}) in entry {index}.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSupportedFormat(byte formatA)
    {
        return formatA is GxTfCmpr or GxTfRgba8;
    }
}
