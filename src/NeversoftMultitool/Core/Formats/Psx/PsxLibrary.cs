namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Result of extracting textures from a single PSX file.
/// </summary>
public sealed class PsxExtractionResult
{
    public int TotalTextures { get; set; }
    public int TexturesWritten { get; set; }
    public int PlaceholdersSkipped { get; set; }
    public bool Success => TotalTextures > 0 && TexturesWritten == TotalTextures;
    public bool Skipped => TotalTextures == 0;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Extracts textures from Neversoft PSX model files.
/// </summary>
public static class PsxLibrary
{
    private static readonly byte[][] ValidMagicNumbers =
    [
        [0x04, 0x00, 0x02, 0x00],
        [0x03, 0x00, 0x02, 0x00],
        [0x06, 0x00, 0x02, 0x00]
    ];

    /// <summary>
    /// Extracts all textures from a PSX file.
    /// </summary>
    public static PsxExtractionResult ExtractTextures(string inputFile, string outputDir, bool createSubDirs,
        bool writeDds = true)
    {
        var result = new PsxExtractionResult();
        var filename = Path.GetFileName(inputFile);

        try
        {
            using var stream = File.OpenRead(inputFile);
            using var reader = new BinaryReader(stream);

            // Validate magic number
            var magic = reader.ReadBytes(4);
            if (!IsValidMagic(magic))
            {
                result.ErrorMessage = "Invalid PSX file magic number";
                return result;
            }

            SkipModelData(reader);
            ReadTextureInfo(reader);
            var palette4Bit = ReadPalettes(reader, 16);
            var palette8Bit = ReadPalettes(reader, 256);

            var numActualTex = reader.ReadUInt32();
            if (numActualTex == 0xFFFFFFFF)
            {
                // v6 extended header: skip detail texture and cubemap references
                var detailCount = reader.ReadUInt32();
                reader.ReadBytes((int)detailCount * 36); // 32-byte name + 4-byte flags

                var cubemapCount = reader.ReadUInt32();
                reader.ReadBytes((int)cubemapCount * 36);

                numActualTex = reader.ReadUInt32(); // the real texture count
            }

            result.TotalTextures = (int)numActualTex;

            // Skip unknown data
            for (var i = 0; i < numActualTex; i++)
            {
                reader.ReadBytes(4);
            }

            for (var i = 0; i < numActualTex; i++)
            {
                var header = GetTextureHeader(reader);

                // Validate 16-bit texture headers: if the format code is unrecognized
                // or the data would extend past EOF, stop — the header is corrupt
                // and all subsequent reads will be garbage.
                if (header.PalSize == 65536)
                {
                    var formatType = (int)(header.PixelFormat & 0xFF00);
                    if (!PvrTextureDecoder.IsSupportedFormat(formatType)
                        || header.TextureOffset + header.Size > reader.BaseStream.Length)
                    {
                        result.TotalTextures = result.TexturesWritten;
                        result.ErrorMessage = $"Unrecognized texture format 0x{header.PixelFormat:X} at offset 0x{header.Offset:X}";
                        break;
                    }
                }

                if (IsPlaceholderTexture(header))
                {
                    reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
                    result.TotalTextures--;
                    result.PlaceholdersSkipped++;
                    continue;
                }

                byte[]? pixels = null;

                if (header.PalSize == 16)
                {
                    pixels = Ps1TextureDecoder.Extract4BitTexture(reader, header, palette4Bit);
                }
                else if (header.PalSize == 256)
                {
                    pixels = Ps1TextureDecoder.Extract8BitTexture(reader, header, palette8Bit);
                }
                else if (header.PalSize == 65536)
                {
                    pixels = Extract16BitTexture(reader, header, filename, outputDir, createSubDirs, writeDds);
                }

                if (pixels != null)
                {
                    WriteToPng(filename, outputDir, createSubDirs, header, pixels);
                    result.TexturesWritten++;
                }
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Extracts a 16-bit PVR texture, writing DDS output with mip levels when available.
    /// </summary>
    private static byte[]? Extract16BitTexture(BinaryReader reader, PsxTextureHeader header,
        string filename, string outputDir, bool createSubDirs, bool writeDds)
    {
        var paletteType = (int)(header.PixelFormat & 0xFF00);
        var hasMips = paletteType is 0x200 or 0x400;

        if (hasMips)
        {
            // Mipmapped: decode all levels for DDS, use main surface for PNG
            var mipChain = PvrTextureDecoder.Extract16BitTextureWithMips(reader, header);
            if (mipChain == null) return null;

            if (writeDds)
            {
                var colorFormat = ColorHelpers.Get16BppColorFormat(header.PixelFormat);
                var ddsPath = GetOutputPath(filename, outputDir, createSubDirs, header, ".dds");
                DdsWriter.WriteDds(ddsPath, colorFormat, mipChain);
            }

            return ColorHelpers.Convert16BitTextureToRgba(
                header.PixelFormat, header.Width, header.Height, mipChain.MainSurface);
        }
        else
        {
            // Non-mipmapped: single surface DDS
            var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
            if (textureBuffer == null) return null;

            if (writeDds)
            {
                var colorFormat = ColorHelpers.Get16BppColorFormat(header.PixelFormat);
                var ddsPath = GetOutputPath(filename, outputDir, createSubDirs, header, ".dds");
                DdsWriter.WriteDds(ddsPath, header.Width, header.Height, colorFormat, textureBuffer);
            }

            return ColorHelpers.Convert16BitTextureToRgba(
                header.PixelFormat, header.Width, header.Height, textureBuffer);
        }
    }

    private static string GetOutputPath(string filename, string outputDir, bool createSubDirs,
        PsxTextureHeader header, string extension)
    {
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        var targetDir = createSubDirs ? Path.Combine(outputDir, filenameWithoutExt) : outputDir;
        var resolvedName = QbKey.TryResolve(header.Hash);
        var textureName = resolvedName != null
            ? $"{filenameWithoutExt}_{resolvedName}"
            : $"{filenameWithoutExt}_{header.Offset:X8}";
        return Path.Combine(targetDir, textureName + extension);
    }

    /// <summary>
    /// Writes a texture to a PNG file.
    /// </summary>
    private static void WriteToPng(string filename, string outputDir, bool createSubDirs,
        PsxTextureHeader header, byte[] pixels)
    {
        var outputPath = GetOutputPath(filename, outputDir, createSubDirs, header, ".png");

        byte[] finalPixels;
        if (header.PalSize != 65536)
        {
            // PS1 textures need pixel fixing
            finalPixels = ColorHelpers.FixPixelData(header.Width, header.Height, pixels);
        }
        else
        {
            // 16-bit textures are already in correct format
            finalPixels = pixels;
        }

        ImageWriter.WritePng(outputPath, header.Width, header.Height, finalPixels);
    }

    private static bool IsPlaceholderTexture(PsxTextureHeader header)
    {
        return header.PalSize == 65536
            && header.Hash == 0
            && header.Width == 16
            && header.Height == 16
            && header.PixelFormat == 0x201
            && header.Size == 692;
    }

    internal static bool IsValidMagic(byte[] magic) =>
        ValidMagicNumbers.Any(valid => magic.AsSpan().SequenceEqual(valid));

    /// <summary>
    /// Skip over model data to get to the texture information.
    /// </summary>
    internal static void SkipModelData(BinaryReader reader)
    {
        var ptrMeta = reader.ReadUInt32();
        var objCount = reader.ReadUInt32();

        // Objects are 36 bytes each
        for (var i = 0; i < objCount; i++)
        {
            reader.ReadBytes(36);
        }

        // Determine number of meshes
        var meshCount = reader.ReadUInt32();

        // Skip to the tagged chunks
        reader.BaseStream.Seek(ptrMeta, SeekOrigin.Begin);
        var chunkCount = -1;
        while (true)
        {
            var magic = reader.ReadBytes(4);
            chunkCount++;
            if (magic[0] != 0xFF || magic[1] != 0xFF || magic[2] != 0xFF || magic[3] != 0xFF)
            {
                var unkLength = reader.ReadUInt32();
                reader.ReadBytes((int)unkLength);
                if (chunkCount > 16)
                {
                    throw new InvalidOperationException("Unable to parse PSX texture library, cannot find texture data");
                }
            }
            else
            {
                break;
            }
        }

        // Skip model names list
        for (var i = 0; i < meshCount; i++)
        {
            reader.ReadBytes(4);
        }
    }

    /// <summary>
    /// Read texture information from the PSX file.
    /// </summary>
    internal static uint[] ReadTextureInfo(BinaryReader reader)
    {
        var numTex = reader.ReadUInt32();
        var texNames = new uint[numTex];
        for (var i = 0; i < numTex; i++)
        {
            texNames[i] = reader.ReadUInt32();
        }
        return texNames;
    }

    /// <summary>
    /// Read palettes from the PSX file.
    /// </summary>
    internal static List<PsxPalette> ReadPalettes(BinaryReader reader, int numColors)
    {
        var numTextures = reader.ReadUInt32();
        var palettes = new List<PsxPalette>((int)numTextures);

        for (var i = 0; i < numTextures; i++)
        {
            var texId = reader.ReadUInt32();
            var colorData = new ushort[numColors];
            for (var c = 0; c < numColors; c++)
            {
                colorData[c] = reader.ReadUInt16();
            }
            palettes.Add(new PsxPalette { TexId = texId, ColorData = colorData });
        }

        return palettes;
    }

    /// <summary>
    /// Read the texture header from the PSX file.
    /// </summary>
    internal static PsxTextureHeader GetTextureHeader(BinaryReader reader)
    {
        var header = new PsxTextureHeader
        {
            Offset = reader.BaseStream.Position,
            Unk = reader.ReadUInt32(),
            PalSize = reader.ReadUInt32(),
            Hash = reader.ReadUInt32(),
            Index = reader.ReadUInt32(),
            Width = reader.ReadUInt16(),
            Height = reader.ReadUInt16()
        };

        // 16-bit textures have additional header fields
        if (header.PalSize == 65536)
        {
            header.PixelFormat = reader.ReadUInt32();
            header.Size = reader.ReadUInt32();
        }

        header.TextureOffset = reader.BaseStream.Position;
        return header;
    }

    /// <summary>
    /// Extracts a single texture by hash from a PSX file, returning in-memory RGBA pixels.
    /// Returns null if the hash is not found or the texture can't be decoded.
    /// </summary>
    internal static (byte[] Rgba, int Width, int Height)? ExtractTextureByHash(
        string psxFilePath, uint targetHash, List<string>? diagnostics = null)
    {
        try
        {
            using var stream = File.OpenRead(psxFilePath);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadBytes(4);
            if (!IsValidMagic(magic))
            {
                diagnostics?.Add($"{Path.GetFileName(psxFilePath)}: invalid magic");
                return null;
            }

            SkipModelData(reader);
            ReadTextureInfo(reader);
            var palette4Bit = ReadPalettes(reader, 16);
            var palette8Bit = ReadPalettes(reader, 256);

            var numActualTex = reader.ReadUInt32();
            if (numActualTex == 0xFFFFFFFF)
            {
                // v6 extended header: skip detail texture and cubemap references
                var detailCount = reader.ReadUInt32();
                reader.ReadBytes((int)detailCount * 36);

                var cubemapCount = reader.ReadUInt32();
                reader.ReadBytes((int)cubemapCount * 36);

                numActualTex = reader.ReadUInt32();
            }

            // Skip unknown data
            for (var i = 0; i < numActualTex; i++)
                reader.ReadBytes(4);

            return FindAndDecodeTexture(reader, (int)numActualTex, targetHash,
                palette4Bit, palette8Bit, diagnostics, Path.GetFileName(psxFilePath));
        }
        catch (Exception ex)
        {
            diagnostics?.Add($"{Path.GetFileName(psxFilePath)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static (byte[] Rgba, int Width, int Height)? FindAndDecodeTexture(
        BinaryReader reader, int textureCount, uint targetHash,
        List<PsxPalette> palette4Bit, List<PsxPalette> palette8Bit,
        List<string>? diagnostics, string fileName)
    {
        for (var i = 0; i < textureCount; i++)
        {
            var header = GetTextureHeader(reader);

            // Validate 16-bit texture headers before attempting to skip/decode
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

            if (header.Hash != targetHash)
            {
                SkipTextureData(reader, header);
                continue;
            }

            // Hash matched
            var pixels = DecodeTexture(reader, header, palette4Bit, palette8Bit);
            if (pixels == null)
            {
                diagnostics?.Add($"{fileName}: hash found at tex {i} (pal={header.PalSize}) but decode returned null");
                return null;
            }

            var finalPixels = header.PalSize != 65536
                ? ColorHelpers.FixPixelData(header.Width, header.Height, pixels)
                : pixels;

            return (finalPixels, header.Width, header.Height);
        }

        diagnostics?.Add($"{fileName}: hash not found in {textureCount} textures");
        return null;
    }

    private static byte[]? DecodeTexture(BinaryReader reader, PsxTextureHeader header,
        List<PsxPalette> palette4Bit, List<PsxPalette> palette8Bit)
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
            header.PixelFormat, header.Width, header.Height, textureBuffer);
    }

    /// <summary>
    /// Skips past texture data without decoding it.
    /// </summary>
    private static void SkipTextureData(BinaryReader reader, PsxTextureHeader header)
    {
        if (header.PalSize == 65536)
        {
            // 16-bit textures have explicit size
            reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
        }
        else if (header.PalSize == 16)
        {
            // 4-bit: padded width / 2 * height + optional padding
            var padWidth = ((header.Width + 0x3) & ~0x3) >> 1;
            var padding = GetPaddingAmount(header, padWidth);
            reader.BaseStream.Seek(header.TextureOffset + padWidth * header.Height + padding,
                SeekOrigin.Begin);
        }
        else if (header.PalSize == 256)
        {
            // 8-bit: padded width * height + optional padding
            var padWidth = (header.Width + 0x1) & ~0x1;
            var padding = GetPaddingAmount(header, padWidth);
            reader.BaseStream.Seek(header.TextureOffset + padWidth * header.Height + padding,
                SeekOrigin.Begin);
        }
    }

    private static int GetPaddingAmount(PsxTextureHeader header, int padWidth)
    {
        if (header.Height % 2 != 0)
            return padWidth % 4 != 0 ? 2 : 0;
        return 0;
    }
}
