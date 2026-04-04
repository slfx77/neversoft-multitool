using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture.Ps1;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Core.Formats.Texture.Psx;

/// <summary>
///     Extracts textures from Neversoft PSX model files.
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
    ///     Extracts all textures from a PSX file.
    /// </summary>
    public static PsxExtractionResult ExtractTextures(string inputFile, string outputDir, bool createSubDirs,
        bool writeDds = true, bool writeMipAtlas = false)
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
            var texNames = ReadTextureInfo(reader);
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

            // Skip texture data top pointers (uint32 offsets to each texture's pixel data)
            for (var i = 0; i < numActualTex; i++)
            {
                reader.ReadBytes(4);
            }

            for (var i = 0; i < numActualTex; i++)
            {
                var header = GetTextureHeader(reader);
                // Use header.Index (textureIndex) to look up name hash, not sequential position i.
                // Confirmed by Ghidra decompilation: textureHashes[puVar14[3]] where [3] = Index field.
                var nameHash = header.Index < texNames.Length ? texNames[header.Index] : 0u;

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
                        result.ErrorMessage =
                            $"Unrecognized texture format 0x{header.PixelFormat:X} at offset 0x{header.Offset:X}";
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
                    pixels = Extract16BitTexture(reader, header, filename, outputDir, createSubDirs,
                        new OutputOptions(writeDds, writeMipAtlas), nameHash);
                }

                if (pixels != null)
                {
                    WriteToPng(filename, outputDir, createSubDirs, header, nameHash, pixels);
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
    ///     Extracts a 16-bit PVR texture, writing DDS output with mip levels when available.
    ///     For mipmapped textures, also writes a mip atlas PNG showing all levels side by side.
    /// </summary>
    private static byte[]? Extract16BitTexture(BinaryReader reader, PsxTextureHeader header,
        string filename, string outputDir, bool createSubDirs, OutputOptions output, uint nameHash)
    {
        var paletteType = (int)(header.PixelFormat & 0xFF00);
        var hasMips = paletteType is 0x200 or 0x400;

        if (hasMips)
        {
            // Mipmapped: decode all levels for DDS, use main surface for PNG
            var mipChain = PvrTextureDecoder.Extract16BitTextureWithMips(reader, header);
            if (mipChain == null) return null;

            if (output.WriteDds)
            {
                var colorFormat = ColorHelpers.Get16BppColorFormat(header.PixelFormat);
                var ddsPath = GetOutputPath(filename, outputDir, createSubDirs, header, nameHash, ".dds");
                DdsWriter.WriteDds(ddsPath, colorFormat, mipChain);
            }

            if (output.WriteMipAtlas)
            {
                var (atlasRgba, atlasW, atlasH) = mipChain.ToAtlasRgba(header.PixelFormat);
                var atlasPath = GetOutputPath(filename, outputDir, createSubDirs, header, nameHash, "_mips.png");
                ImageWriter.WritePng(atlasPath, atlasW, atlasH, atlasRgba);
            }

            return ColorHelpers.Convert16BitTextureToRgba(
                header.PixelFormat, header.Width, header.Height, mipChain.MainSurface);
        }

        // Non-mipmapped: single surface DDS
        var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
        if (textureBuffer == null) return null;

        if (output.WriteDds)
        {
            var colorFormat = ColorHelpers.Get16BppColorFormat(header.PixelFormat);
            var ddsPath = GetOutputPath(filename, outputDir, createSubDirs, header, nameHash, ".dds");
            DdsWriter.WriteDds(ddsPath, header.Width, header.Height, colorFormat, textureBuffer);
        }

        return ColorHelpers.Convert16BitTextureToRgba(
            header.PixelFormat, header.Width, header.Height, textureBuffer);
    }

    private static string GetOutputPath(string filename, string outputDir, bool createSubDirs,
        PsxTextureHeader header, uint nameHash, string extension)
    {
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        var targetDir = createSubDirs ? Path.Combine(outputDir, filenameWithoutExt) : outputDir;
        var resolvedName = QbKey.QbKey.TryResolve(nameHash);
        var textureName = resolvedName != null
            ? $"{filenameWithoutExt}_{resolvedName}"
            : $"{filenameWithoutExt}_{header.Offset:X8}";
        return Path.Combine(targetDir, textureName + extension);
    }

    /// <summary>
    ///     Writes a texture to a PNG file.
    /// </summary>
    private static void WriteToPng(string filename, string outputDir, bool createSubDirs,
        PsxTextureHeader header, uint nameHash, byte[] pixels)
    {
        var outputPath = GetOutputPath(filename, outputDir, createSubDirs, header, nameHash, ".png");

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
               && header.TexId == 0
               && header.Width == 16
               && header.Height == 16
               && header.PixelFormat == 0x201
               && header.Size == 692;
    }

    internal static bool IsValidMagic(byte[] magic)
    {
        return ValidMagicNumbers.Any(valid => magic.AsSpan().SequenceEqual(valid));
    }

    /// <summary>
    ///     Skip over model data to get to the texture information.
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
                    throw new InvalidOperationException(
                        "Unable to parse PSX texture library, cannot find texture data");
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
    ///     Read texture information from the PSX file.
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
    ///     Read palettes from the PSX file.
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
    ///     Read the texture header from the PSX file.
    /// </summary>
    internal static PsxTextureHeader GetTextureHeader(BinaryReader reader)
    {
        var header = new PsxTextureHeader
        {
            Offset = reader.BaseStream.Position,
            Unk = reader.ReadUInt32(),
            PalSize = reader.ReadUInt32(),
            TexId = reader.ReadUInt32(),
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
    ///     Extracts a single texture by hash from a PSX file, returning in-memory RGBA pixels.
    ///     Returns null if the hash is not found or the texture can't be decoded.
    /// </summary>
    internal static (byte[] Rgba, int Width, int Height)? ExtractTextureByHash(
        string psxFilePath, uint targetHash, List<string>? diagnostics = null)
    {
        return PsxLibraryLookup.ExtractTextureByHash(psxFilePath, targetHash, diagnostics);
    }

    /// <summary>
    ///     Enumerates all textures in a PSX file without extracting pixel data.
    ///     Returns headers paired with their name hashes from the texture name list.
    /// </summary>
    public static List<(PsxTextureHeader Header, uint NameHash)> EnumerateTextures(string inputFile)
    {
        return PsxLibraryLookup.EnumerateTextures(inputFile);
    }

    /// <summary>
    ///     Returns a human-readable description of a texture's palette/format type.
    /// </summary>
    public static string DescribePaletteType(PsxTextureHeader header)
    {
        if (header.PalSize == 16) return "4-bit (PS1)";
        if (header.PalSize == 256) return "8-bit (PS1)";
        if (header.PalSize != 65536) return $"Unknown ({header.PalSize})";

        var colorFormat = ColorHelpers.Get16BppColorFormat(header.PixelFormat);
        var colorName = colorFormat switch
        {
            ColorFormat.Argb1555 => "ARGB1555",
            ColorFormat.Rgb565 => "RGB565",
            ColorFormat.Argb4444 => "ARGB4444",
            _ => "Unknown"
        };

        var encoding = (int)(header.PixelFormat & 0xFF00) switch
        {
            0x100 => "twiddled",
            0x200 => "twiddled+mip",
            0x300 => "VQ",
            0x400 => "VQ+mip",
            0x900 => "rectangle",
            0xD00 => "twiddled",
            _ => $"0x{header.PixelFormat & 0xFF00:X}"
        };

        return $"16-bit ({colorName}, {encoding})";
    }
}
