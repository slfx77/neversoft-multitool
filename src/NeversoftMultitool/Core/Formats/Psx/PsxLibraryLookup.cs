namespace NeversoftMultitool.Core.Formats.Psx;

internal static class PsxLibraryLookup
{
    public static (byte[] Rgba, int Width, int Height)? ExtractTextureByHash(
        string psxFilePath,
        uint targetHash,
        List<string>? diagnostics = null)
    {
        try
        {
            using var stream = File.OpenRead(psxFilePath);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadBytes(4);
            if (!PsxLibrary.IsValidMagic(magic))
            {
                diagnostics?.Add($"{Path.GetFileName(psxFilePath)}: invalid magic");
                return null;
            }

            PsxLibrary.SkipModelData(reader);
            var texNames = PsxLibrary.ReadTextureInfo(reader);
            var palette4Bit = PsxLibrary.ReadPalettes(reader, 16);
            var palette8Bit = PsxLibrary.ReadPalettes(reader, 256);

            var textureCount = ReadTextureCount(reader);
            var targetIndex = Array.IndexOf(texNames, targetHash);
            if (targetIndex < 0)
            {
                diagnostics?.Add(
                    $"{Path.GetFileName(psxFilePath)}: hash 0x{targetHash:X8} not found in texture name list");
                return null;
            }

            for (var i = 0; i < textureCount; i++)
                reader.ReadBytes(4);

            return FindAndDecodeTexture(
                reader,
                (int)textureCount,
                targetIndex,
                palette4Bit,
                palette8Bit,
                diagnostics,
                Path.GetFileName(psxFilePath));
        }
        catch (Exception ex)
        {
            diagnostics?.Add($"{Path.GetFileName(psxFilePath)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static List<(PsxTextureHeader Header, uint NameHash)> EnumerateTextures(string inputFile)
    {
        using var stream = File.OpenRead(inputFile);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(4);
        if (!PsxLibrary.IsValidMagic(magic))
            return [];

        PsxLibrary.SkipModelData(reader);
        var texNames = PsxLibrary.ReadTextureInfo(reader);
        PsxLibrary.ReadPalettes(reader, 16);
        PsxLibrary.ReadPalettes(reader, 256);

        var textureCount = ReadTextureCount(reader);
        for (var i = 0; i < textureCount; i++)
            reader.ReadBytes(4);

        var results = new List<(PsxTextureHeader, uint)>((int)textureCount);
        for (var i = 0; i < textureCount; i++)
        {
            var header = PsxLibrary.GetTextureHeader(reader);
            var nameHash = header.Index < texNames.Length ? texNames[header.Index] : 0u;
            results.Add((header, nameHash));
            SkipTextureData(reader, header);
        }

        return results;
    }

    private static uint ReadTextureCount(BinaryReader reader)
    {
        var textureCount = reader.ReadUInt32();
        if (textureCount != 0xFFFFFFFF)
            return textureCount;

        var detailCount = reader.ReadUInt32();
        reader.ReadBytes((int)detailCount * 36);

        var cubemapCount = reader.ReadUInt32();
        reader.ReadBytes((int)cubemapCount * 36);

        return reader.ReadUInt32();
    }

    private static (byte[] Rgba, int Width, int Height)? FindAndDecodeTexture(
        BinaryReader reader,
        int textureCount,
        int targetIndex,
        List<PsxPalette> palette4Bit,
        List<PsxPalette> palette8Bit,
        List<string>? diagnostics,
        string fileName)
    {
        for (var i = 0; i < textureCount; i++)
        {
            var header = PsxLibrary.GetTextureHeader(reader);
            if (header.PalSize == 65536)
            {
                var formatType = (int)(header.PixelFormat & 0xFF00);
                if (!PvrTextureDecoder.IsSupportedFormat(formatType)
                    || header.TextureOffset + header.Size > reader.BaseStream.Length)
                {
                    diagnostics?.Add($"{fileName}: corrupt header at texture {i}");
                    return null;
                }
            }

            if (header.Index != (uint)targetIndex)
            {
                SkipTextureData(reader, header);
                continue;
            }

            var pixels = DecodeTexture(reader, header, palette4Bit, palette8Bit);
            if (pixels == null)
            {
                diagnostics?.Add($"{fileName}: texture at index {i} (pal={header.PalSize}) decode returned null");
                return null;
            }

            var finalPixels = header.PalSize != 65536
                ? ColorHelpers.FixPixelData(header.Width, header.Height, pixels)
                : pixels;

            return (finalPixels, header.Width, header.Height);
        }

        diagnostics?.Add($"{fileName}: target index {targetIndex} not reached in {textureCount} textures");
        return null;
    }

    private static byte[]? DecodeTexture(
        BinaryReader reader,
        PsxTextureHeader header,
        List<PsxPalette> palette4Bit,
        List<PsxPalette> palette8Bit)
    {
        if (header.PalSize == 16)
            return Ps1TextureDecoder.Extract4BitTexture(reader, header, palette4Bit);
        if (header.PalSize == 256)
            return Ps1TextureDecoder.Extract8BitTexture(reader, header, palette8Bit);
        if (header.PalSize != 65536)
            return null;

        var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
        if (textureBuffer == null)
            return null;

        return ColorHelpers.Convert16BitTextureToRgba(
            header.PixelFormat,
            header.Width,
            header.Height,
            textureBuffer);
    }

    private static void SkipTextureData(BinaryReader reader, PsxTextureHeader header)
    {
        if (header.PalSize == 65536)
        {
            reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
            return;
        }

        if (header.PalSize == 16)
        {
            var paddedWidth = ((header.Width + 0x3) & ~0x3) >> 1;
            var padding = GetPaddingAmount(header, paddedWidth);
            reader.BaseStream.Seek(header.TextureOffset + paddedWidth * header.Height + padding, SeekOrigin.Begin);
            return;
        }

        if (header.PalSize == 256)
        {
            var paddedWidth = (header.Width + 0x1) & ~0x1;
            var padding = GetPaddingAmount(header, paddedWidth);
            reader.BaseStream.Seek(header.TextureOffset + paddedWidth * header.Height + padding, SeekOrigin.Begin);
        }
    }

    private static int GetPaddingAmount(PsxTextureHeader header, int paddedWidth)
    {
        if (header.Height % 2 != 0)
            return paddedWidth % 4 != 0 ? 2 : 0;

        return 0;
    }
}
