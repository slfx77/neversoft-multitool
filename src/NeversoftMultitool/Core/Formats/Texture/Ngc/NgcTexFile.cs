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
    private const byte MaximumDimensionExponent = 10;
    private const int HeaderSize = 8;
    private const int EntrySize = 32;

    private enum PixelLayout
    {
        Cmpr,
        CmprPadded,
        C8,
        Rgba8
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

    public static Ps2TexResult Parse(ReadOnlySpan<byte> data)
    {
        // Bare .img.ngc files are a single 32-byte record with no dictionary header.
        if (IsBareRecord(data))
        {
            if (!TryReadEntryAt(data, 0, 0, out var bareEntry, out var bareError))
                return Ps2TexResult.Fail(bareError);
            return DecodeEntries(data, [bareEntry], usePaddingDimensions: true);
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

        return DecodeEntries(data, entries, usePaddingDimensions: false);
    }

    /// <summary>Returns true for a bare .img.ngc record (no dictionary header).</summary>
    public static bool IsBareRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length < EntrySize || data[0] != RecordVersion || data[1] != RecordDepth)
            return false;
        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return dataOffset >= EntrySize && dataOffset <= (uint)data.Length;
    }

    private static Ps2TexResult DecodeEntries(
        ReadOnlySpan<byte> data,
        List<NgcTexEntry> entries,
        bool usePaddingDimensions)
    {
        var textures = new List<Ps2Texture>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!IsSupportedFormat(entry.FormatA, entry.FormatB))
            {
                return Ps2TexResult.Fail(
                    $"Unsupported NGC texture format ({entry.FormatA},{entry.FormatB}) in entry {index} (checksum 0x{entry.Checksum:X8}).");
            }

            var dataEnd = (long)entry.DataOffset + entry.DataSize;
            if (dataEnd > data.Length)
            {
                return Ps2TexResult.Fail(
                    $"Texture data for entry {index} extends past end of file (offset {entry.DataOffset}, size {entry.DataSize}).");
            }

            if (!TryResolveDimensions(
                    entry,
                    out var layout,
                    out var width,
                    out var height,
                    out var decodeWidth,
                    out var decodeHeight,
                    out var dimensionError,
                    usePaddingDimensions))
            {
                return Ps2TexResult.Fail(
                    $"NGC texture entry {index} (checksum 0x{entry.Checksum:X8}) has {dimensionError}.");
            }

            byte[] pixels;
            try
            {
                if (entry.FormatA == GxTfCmpr)
                {
                    pixels = NgcTexCmprDecoder.DecodeToRgba(
                        data.Slice(entry.DataOffset, entry.DataSize),
                        decodeWidth,
                        decodeHeight);

                    // DXT5-style alpha ships as a second CMPR chain whose green
                    // channel carries the alpha (same trick THUG GC used, see
                    // Sample/thug Gfx/NGC/NX/texture.cpp).
                    if (entry.AlphaOffset >= 0
                        && entry.AlphaOffset <= data.Length - entry.DataSize)
                    {
                        var alpha = NgcTexCmprDecoder.DecodeToRgba(
                            data.Slice(entry.AlphaOffset, entry.DataSize),
                            decodeWidth,
                            decodeHeight);
                        var pixelCount = checked(decodeWidth * decodeHeight);
                        for (var i = 0; i < pixelCount; i++)
                            pixels[i * 4 + 3] = alpha[i * 4 + 1];
                    }
                }
                else if (layout == PixelLayout.C8)
                {
                    pixels = NgcTexC8Decoder.DecodeToRgba(
                        data.Slice(entry.DataOffset, entry.DataSize),
                        width,
                        height);
                }
                else
                {
                    pixels = NgcTexRgba8Decoder.DecodeToRgba(
                        data.Slice(entry.DataOffset, entry.DataSize),
                        width,
                        height);
                }
            }
            catch (Exception ex)
            {
                return Ps2TexResult.Fail(
                    $"Failed to decode NGC texture entry {index} (checksum 0x{entry.Checksum:X8}): {ex.Message}");
            }

            FlipRows(pixels, decodeWidth, decodeHeight);
            if (layout == PixelLayout.CmprPadded)
                pixels = CropTopLeft(pixels, decodeWidth, width, height);

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

    /// <summary>
    ///     The exponent fields describe a power-of-two storage surface; bytes
    ///     +8/+9 describe the right/bottom padding modulo 256. Wii load screens
    ///     need the modulo form (1024 - (128 + 256) = 640). The payload-size
    ///     identity selects the unique unwrapped dimensions across all 12,127
    ///     measured GameCube/Wii IMG records.
    /// </summary>
    private static bool TryResolveDimensions(
        NgcTexEntry entry,
        out PixelLayout layout,
        out int width,
        out int height,
        out int decodeWidth,
        out int decodeHeight,
        out string error,
        bool usePaddingDimensions)
    {
        // Dictionary entries may carry complete mip chains in DataSize, so the
        // single-surface identities below do not apply to them. Preserve the
        // established dictionary rule; the pad-byte contract is for bare IMG.
        if (!usePaddingDimensions)
        {
            width = entry.Width;
            height = entry.Height;
            decodeWidth = width;
            decodeHeight = height;
            layout = entry.FormatA == GxTfCmpr ? PixelLayout.Cmpr : PixelLayout.Rgba8;
            if (entry.FormatA != GxTfCmpr)
            {
                var c8Bytes = (long)entry.DataSize - NgcTexC8Decoder.PaletteBytes;
                var c8Rows = c8Bytes > 0 && c8Bytes % width == 0
                    ? c8Bytes / width
                    : 0L;
                if (c8Rows > 0 && c8Rows <= height)
                {
                    height = decodeHeight = (int)c8Rows;
                    layout = PixelLayout.C8;
                }
                else
                {
                    var rgbaStride = 4L * width;
                    var rgbaRows = entry.DataSize / rgbaStride;
                    if (rgbaRows > 0 && rgbaRows < height)
                        height = decodeHeight = (int)rgbaRows;
                }
            }

            error = string.Empty;
            return true;
        }

        var matches = new List<(PixelLayout Layout, int Width, int Height)>();

        if (entry.FormatA == GxTfCmpr)
        {
            if (entry.DataSize % 32 == 0)
            {
                var blockArea = entry.DataSize / 32L;
                for (long factor = 1; factor <= blockArea / factor; factor++)
                {
                    if (blockArea % factor != 0)
                        continue;

                    var other = blockArea / factor;
                    AddCmprMatches(entry, factor, other, matches);
                    if (factor != other)
                        AddCmprMatches(entry, other, factor, matches);
                }
            }
        }
        else
        {
            AddLinearMatches(
                entry,
                PixelLayout.C8,
                entry.DataSize - (long)NgcTexC8Decoder.PaletteBytes,
                matches);
            if (entry.DataSize % 4 == 0)
                AddLinearMatches(entry, PixelLayout.Rgba8, entry.DataSize / 4L, matches);
        }

        if (matches.Count == 1)
        {
            (layout, width, height) = matches[0];
            decodeWidth = width;
            decodeHeight = height;
            error = string.Empty;
            return true;
        }

        // Some CMPR payloads retain the full POT tile surface and use the pad
        // bytes strictly as a display crop. No wrapped (>255) pad occurs in this
        // 210-file corpus class, so the direct byte values are authoritative.
        if (entry.FormatA == GxTfCmpr
            && GetCmprByteCount(entry.Width, entry.Height) == entry.DataSize)
        {
            width = entry.Width - entry.WidthPadding;
            height = entry.Height - entry.HeightPadding;
            if (width > 0 && height > 0)
            {
                layout = PixelLayout.CmprPadded;
                decodeWidth = entry.Width;
                decodeHeight = entry.Height;
                error = string.Empty;
                return true;
            }
        }

        layout = default;
        width = height = decodeWidth = decodeHeight = 0;
        error = matches.Count == 0
            ? $"no size-compatible dimensions for {entry.Width}x{entry.Height} storage, " +
              $"pad bytes {entry.WidthPadding},{entry.HeightPadding}, and {entry.DataSize} data bytes"
            : $"ambiguous dimensions ({matches.Count} size-compatible candidates)";
        return false;
    }

    private static void AddLinearMatches(
        NgcTexEntry entry,
        PixelLayout layout,
        long pixelArea,
        List<(PixelLayout Layout, int Width, int Height)> matches)
    {
        if (pixelArea <= 0)
            return;

        for (long factor = 1; factor <= pixelArea / factor; factor++)
        {
            if (pixelArea % factor != 0)
                continue;

            var other = pixelArea / factor;
            AddMatchIfCandidate(entry, layout, factor, other, matches);
            if (factor != other)
                AddMatchIfCandidate(entry, layout, other, factor, matches);
        }
    }

    private static void AddCmprMatches(
        NgcTexEntry entry,
        long blockWidth,
        long blockHeight,
        List<(PixelLayout Layout, int Width, int Height)> matches)
    {
        var minimumWidth = (blockWidth - 1) * 8 + 1;
        var maximumWidth = blockWidth * 8;
        var minimumHeight = (blockHeight - 1) * 8 + 1;
        var maximumHeight = blockHeight * 8;

        // Each interval contains at most eight dimensions. This is equivalent
        // to cross-producting every modulo-256 padding candidate, but its work
        // is bounded by the payload's factor count instead of the POT exponent.
        for (var width = minimumWidth; width <= maximumWidth; width++)
        for (var height = minimumHeight; height <= maximumHeight; height++)
            AddMatchIfCandidate(entry, PixelLayout.Cmpr, width, height, matches);
    }

    private static void AddMatchIfCandidate(
        NgcTexEntry entry,
        PixelLayout layout,
        long width,
        long height,
        List<(PixelLayout Layout, int Width, int Height)> matches)
    {
        if (!IsDimensionCandidate(entry.Width, entry.WidthPadding, width)
            || !IsDimensionCandidate(entry.Height, entry.HeightPadding, height))
        {
            return;
        }

        var match = (layout, (int)width, (int)height);
        if (!matches.Contains(match))
            matches.Add(match);
    }

    private static bool IsDimensionCandidate(int paddedDimension, byte paddingLowByte, long dimension)
    {
        if (dimension <= 0 || dimension > paddedDimension)
            return false;

        var padding = paddedDimension - dimension;
        return (padding & 0xFF) == paddingLowByte;
    }

    private static long GetCmprByteCount(int width, int height)
    {
        return ((long)width + 7) / 8 * (((long)height + 7) / 8) * 32;
    }

    private static byte[] CropTopLeft(
        byte[] pixels,
        int sourceWidth,
        int targetWidth,
        int targetHeight)
    {
        var croppedByteCount = checked((long)targetWidth * targetHeight * 4);
        if (croppedByteCount > Array.MaxLength)
            throw new InvalidDataException("NGC texture crop exceeds the runtime array limit");

        var cropped = new byte[(int)croppedByteCount];
        var sourceStride = checked(sourceWidth * 4);
        var targetStride = checked(targetWidth * 4);
        for (var y = 0; y < targetHeight; y++)
            pixels.AsSpan(y * sourceStride, targetStride).CopyTo(cropped.AsSpan(y * targetStride));
        return cropped;
    }

    /// <summary>GC textures are stored bottom-up; flip to top-down.</summary>
    private static void FlipRows(byte[] pixels, int width, int height)
    {
        var stride = checked(width * 4);
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

        var requiredMetadataSize = (long)metadataOffset + (long)textureCount * EntrySize;
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

        var offsetLong = (long)header.MetadataOffset + (long)index * EntrySize;
        if (offsetLong > data.Length - EntrySize)
        {
            error = $"NGC TEX entry {index} is truncated.";
            return false;
        }

        var offset = (int)offsetLong;
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
        if (span[0] != RecordVersion)
        {
            error = $"NGC TEX entry {index} has invalid record version {span[0]}.";
            return false;
        }

        if (span[1] != RecordDepth)
        {
            error = $"NGC TEX entry {index} has invalid record depth {span[1]}.";
            return false;
        }

        var reservedTail = BinaryPrimitives.ReadUInt32BigEndian(span[28..]);
        if (reservedTail != 0)
        {
            error = $"NGC TEX entry {index} has invalid reserved trailer 0x{reservedTail:X8}.";
            return false;
        }

        var checksum = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        var widthPadding = span[8];
        var heightPadding = span[9];
        var widthExponent = span[10];
        var heightExponent = span[11];
        if (widthExponent > MaximumDimensionExponent)
        {
            error = $"NGC TEX entry {index} has invalid width exponent {widthExponent}.";
            return false;
        }

        if (heightExponent > MaximumDimensionExponent)
        {
            error = $"NGC TEX entry {index} has invalid height exponent {heightExponent}.";
            return false;
        }

        var width = 1 << widthExponent;
        var height = 1 << heightExponent;
        var formatA = span[13];
        var formatB = span[14];
        var rawDataSize = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
        var rawDataOffset = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
        var alphaRaw = BinaryPrimitives.ReadUInt32BigEndian(span[24..]);

        var rgbaByteCount = (long)width * height * 4;
        if (rgbaByteCount > Array.MaxLength)
        {
            error = $"NGC TEX entry {index} dimensions {width}x{height} exceed the runtime array limit.";
            return false;
        }

        if (rawDataSize == 0 || rawDataSize > int.MaxValue)
        {
            error = $"NGC TEX entry {index} has invalid data size {rawDataSize}.";
            return false;
        }

        if (rawDataOffset < HeaderSize
            || (ulong)rawDataOffset + rawDataSize > (ulong)data.Length)
        {
            error = $"NGC TEX entry {index} has invalid data range ({rawDataOffset}, {rawDataSize}).";
            return false;
        }

        if (alphaRaw != uint.MaxValue && alphaRaw > int.MaxValue)
        {
            error = $"NGC TEX entry {index} has invalid alpha offset {alphaRaw}.";
            return false;
        }

        var dataSize = (int)rawDataSize;
        var dataOffset = (int)rawDataOffset;
        var alphaOffset = alphaRaw == uint.MaxValue ? -1 : (int)alphaRaw;
        entry = new NgcTexEntry(
            magic,
            checksum,
            width,
            height,
            widthPadding,
            heightPadding,
            formatA,
            formatB,
            dataSize,
            dataOffset,
            alphaOffset);
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
        // All measured dictionaries use (CMPR,12); loose GC/Wii records use
        // (CMPR,4) or (RGBA8/C8,4). Rejecting other pairs prevents a familiar
        // GX format byte inside an unrelated 32-byte record from probing true.
        return (formatA, formatB) is (GxTfCmpr, 12) or (GxTfCmpr, 4) or (GxTfRgba8, 4);
    }
}
