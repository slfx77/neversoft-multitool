namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Result of extracting textures from a single PSX file.
/// </summary>
public sealed class PsxExtractionResult
{
    public int TotalTextures { get; set; }
    public int TexturesWritten { get; set; }
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
    public static PsxExtractionResult ExtractTextures(string inputFile, string outputDir, bool createSubDirs)
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
                result.ErrorMessage = "Unable to parse PSX texture library, PVR-T?";
                return result;
            }

            result.TotalTextures = (int)numActualTex;

            // Skip unknown data
            for (var i = 0; i < numActualTex; i++)
            {
                reader.ReadBytes(4);
            }

            var filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);

            for (var i = 0; i < numActualTex; i++)
            {
                var header = GetTextureHeader(reader);

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
                    var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
                    if (textureBuffer != null)
                    {
                        pixels = ColorHelpers.Convert16BitTextureToRgba(
                            header.PixelFormat, header.Width, header.Height, textureBuffer);
                    }
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
    /// Writes a texture to a PNG file.
    /// </summary>
    private static void WriteToPng(string filename, string outputDir, bool createSubDirs,
        PsxTextureHeader header, byte[] pixels)
    {
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        var targetDir = createSubDirs ? Path.Combine(outputDir, filenameWithoutExt) : outputDir;
        var outputPath = Path.Combine(targetDir, $"{filenameWithoutExt}_{header.Offset:X8}.png");

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

    private static bool IsValidMagic(byte[] magic)
    {
        foreach (var valid in ValidMagicNumbers)
        {
            if (magic.AsSpan().SequenceEqual(valid))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Skip over model data to get to the texture information.
    /// </summary>
    private static void SkipModelData(BinaryReader reader)
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
    private static uint[] ReadTextureInfo(BinaryReader reader)
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
    private static List<PsxPalette> ReadPalettes(BinaryReader reader, int numColors)
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
    private static PsxTextureHeader GetTextureHeader(BinaryReader reader)
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
}
