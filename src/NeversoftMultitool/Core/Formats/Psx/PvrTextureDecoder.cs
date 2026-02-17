namespace NeversoftMultitool.Core.Formats.Psx;

public static class PvrTextureDecoder
{
    private static readonly HashSet<int> SupportedFormats = [0x100, 0x200, 0x300, 0x400, 0x900, 0xD00];

    /// <summary>
    /// Returns true if the given format type code (PixelFormat &amp; 0xFF00) is supported.
    /// </summary>
    public static bool IsSupportedFormat(int formatType) => SupportedFormats.Contains(formatType);

    /// <summary>
    /// Extracts a 16-bit texture from a PowerVR texture file.
    /// Returns a ushort[] texture buffer, or null if unsupported.
    /// </summary>
    public static ushort[]? Extract16BitTexture(BinaryReader reader, PsxTextureHeader header,
        List<string>? outputStrings = null)
    {
        var decompressed = DecompressTexture(reader, header, outputStrings);
        reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
        return decompressed;
    }

    /// <summary>
    /// Decompresses a texture from a PowerVR texture file.
    /// </summary>
    private static ushort[]? DecompressTexture(BinaryReader reader, PsxTextureHeader header,
        List<string>? outputStrings)
    {
        if (header.Height >> 1 == 0)
            return null;

        var paletteType = (int)(header.PixelFormat & 0xFF00);

        return paletteType switch
        {
            0x100 => DecodeTwiddled(reader, header, false),
            0x200 => DecodeTwiddled(reader, header, true),
            0x300 => DecodeTwiddledVq(reader, header, reader.BaseStream.Position, false),
            0x400 => DecodeTwiddledVq(reader, header, reader.BaseStream.Position, true),
            0x900 => DecodeRectangle(reader, header),
            0xD00 => DecodeTwiddled(reader, header, false),
            _ => SkipUnsupported(header, outputStrings, paletteType)
        };
    }

    private static ushort[]? SkipUnsupported(PsxTextureHeader header, List<string>? outputStrings, int paletteType)
    {
        if (!SupportedFormats.Contains(paletteType))
        {
            outputStrings?.Add($"Not implemented yet: 0x{header.PixelFormat:X} - palette type 0x{paletteType:X}.");
        }
        return null;
    }

    /// <summary>
    /// Decodes a twiddled texture.
    /// </summary>
    private static ushort[] DecodeTwiddled(BinaryReader reader, PsxTextureHeader header, bool mipmap)
    {
        var textureBufferSize = header.Width * header.Height;
        var textureBuffer = new ushort[textureBufferSize];

        if (mipmap)
        {
            var mipLevelStartIndex = CalculateMipLevelStartIndex(header.Width) * 2;
            reader.BaseStream.Seek(reader.BaseStream.Position + mipLevelStartIndex, SeekOrigin.Begin);
        }

        var chunkSize = Math.Min(header.Width, header.Height);
        var chunksWide = header.Width / chunkSize;
        var chunksHigh = header.Height / chunkSize;
        var totalChunks = Math.Max(chunksWide, chunksHigh);

        for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            var chunkX = chunkIndex % chunksWide;
            var chunkY = chunkIndex / chunksWide;
            var chunkOffsetX = chunkX * chunkSize;
            var chunkOffsetY = chunkY * chunkSize;

            for (var i = 0; i < chunkSize * chunkSize; i++)
            {
                var destinationIndex = MortonCurve.Morton(i, chunkSize, chunkSize);
                var x = destinationIndex % chunkSize;
                var y = destinationIndex / chunkSize;

                var newX = chunkSize - y - 1;
                newX = chunkSize - newX - 1;
                var newY = x;

                newX += chunkOffsetX;
                newY += chunkOffsetY;

                var updatedDestinationIndex = newY * header.Width + newX;
                var channel = reader.ReadUInt16();

                if (updatedDestinationIndex >= 0 && updatedDestinationIndex < textureBufferSize)
                {
                    textureBuffer[updatedDestinationIndex] = channel;
                }
            }
        }

        return textureBuffer;
    }

    /// <summary>
    /// Decodes a twiddled VQ texture.
    /// </summary>
    private static ushort[] DecodeTwiddledVq(BinaryReader reader, PsxTextureHeader header,
        long textureOffset, bool mipmap)
    {
        var textureBuffer = new ushort[header.Width * header.Height];
        Array.Fill(textureBuffer, (ushort)0xFF);
        var widthTimesTwo = header.Width * 2;

        var mipLevelOffset = mipmap ? CalculateMipLevelStartIndex(header.Width / 2) : 0;

        for (var row = 0; row < header.Height / 2; row++)
        {
            for (var col = 0; col < header.Width / 2; col++)
            {
                var colorOffset = MortonCurve.Interleave(row, col) + mipLevelOffset;
                var colorBlock = GetColorBlock(reader, textureOffset, colorOffset);

                var baseIndex = row * widthTimesTwo + col * 2;
                if (baseIndex < textureBuffer.Length)
                    textureBuffer[baseIndex] = colorBlock[0];
                if (baseIndex + 1 < textureBuffer.Length)
                    textureBuffer[baseIndex + 1] = colorBlock[2];
                if (baseIndex + header.Width < textureBuffer.Length)
                    textureBuffer[baseIndex + header.Width] = colorBlock[1];
                if (baseIndex + header.Width + 1 < textureBuffer.Length)
                    textureBuffer[baseIndex + header.Width + 1] = colorBlock[3];
            }
        }

        return textureBuffer;
    }

    /// <summary>
    /// Decodes a rectangular texture.
    /// </summary>
    private static ushort[] DecodeRectangle(BinaryReader reader, PsxTextureHeader header)
    {
        var goal = header.Width * header.Height;
        var textureBuffer = new ushort[goal];

        for (var counter = 0; counter < goal; counter++)
        {
            textureBuffer[counter] = reader.ReadUInt16();
        }

        return textureBuffer;
    }

    /// <summary>
    /// Calculates the starting index for a mip level.
    /// </summary>
    private static int CalculateMipLevelStartIndex(int mipLevelDimension)
    {
        var startIndex = 1;
        while (mipLevelDimension > 0)
        {
            mipLevelDimension >>= 1;
            startIndex += mipLevelDimension * mipLevelDimension;
        }
        return startIndex;
    }

    /// <summary>
    /// Reads a color block from the texture.
    /// </summary>
    private static ushort[] GetColorBlock(BinaryReader reader, long textureOffset, int colorOffset)
    {
        reader.BaseStream.Seek(textureOffset + 0x800 + colorOffset, SeekOrigin.Begin);
        var paletteOffset = reader.ReadByte();

        reader.BaseStream.Seek(textureOffset + 8 * paletteOffset, SeekOrigin.Begin);
        var pixels = new ushort[4];
        for (var i = 0; i < 4; i++)
        {
            pixels[i] = reader.ReadUInt16();
        }
        return pixels;
    }

    // ── Mip-chain decoding (new methods, existing methods unchanged) ──────────

    /// <summary>
    /// Extracts a 16-bit texture with all mip levels.
    /// Returns null for non-mipmapped formats; caller falls back to single-surface path.
    /// </summary>
    internal static PvrMipChain? Extract16BitTextureWithMips(BinaryReader reader, PsxTextureHeader header)
    {
        var paletteType = (int)(header.PixelFormat & 0xFF00);

        var result = paletteType switch
        {
            0x200 => DecodeTwiddledAllLevels(reader, header),
            0x400 => DecodeTwiddledVqAllLevels(reader, header),
            _ => (PvrMipChain?)null
        };

        reader.BaseStream.Seek(header.TextureOffset + header.Size, SeekOrigin.Begin);
        return result;
    }

    /// <summary>
    /// Decodes all mip levels of a twiddled mipmapped texture (format 0x200).
    /// PVR stores smallest-to-largest; we reverse to DDS convention (largest first).
    /// Layout: [1-pixel sentinel][1x1][2x2][4x4]...[W/2×W/2][W×W main]
    /// </summary>
    private static PvrMipChain DecodeTwiddledAllLevels(BinaryReader reader, PsxTextureHeader header)
    {
        var levels = new List<ushort[]>();
        var dataStart = reader.BaseStream.Position;

        // Skip the 1-pixel sentinel (2 bytes)
        reader.BaseStream.Seek(dataStart + 2, SeekOrigin.Begin);

        // Decode mip levels from 1x1 up to W/2 × W/2, then the main surface
        var dim = 1;
        while (dim <= header.Width)
        {
            var level = DecodeTwiddledSurface(reader, dim, dim);
            levels.Add(level);
            dim *= 2;
        }

        // Reverse: PVR stores smallest-first, DDS needs largest-first
        levels.Reverse();

        return new PvrMipChain
        {
            Levels = levels,
            Width = header.Width,
            Height = header.Height
        };
    }

    /// <summary>
    /// Decodes a single twiddled square surface at the current reader position.
    /// Same Morton-curve logic as DecodeTwiddled but for arbitrary dimensions.
    /// </summary>
    private static ushort[] DecodeTwiddledSurface(BinaryReader reader, int width, int height)
    {
        var textureBufferSize = width * height;
        var textureBuffer = new ushort[textureBufferSize];

        var chunkSize = Math.Min(width, height);
        var chunksWide = width / chunkSize;
        var chunksHigh = height / chunkSize;
        var totalChunks = Math.Max(chunksWide, chunksHigh);

        for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            var chunkX = chunkIndex % chunksWide;
            var chunkY = chunkIndex / chunksWide;
            var chunkOffsetX = chunkX * chunkSize;
            var chunkOffsetY = chunkY * chunkSize;

            for (var i = 0; i < chunkSize * chunkSize; i++)
            {
                var destinationIndex = MortonCurve.Morton(i, chunkSize, chunkSize);
                var x = destinationIndex % chunkSize;
                var y = destinationIndex / chunkSize;

                // Coordinate transform (matches existing DecodeTwiddled logic)
                var newX = chunkSize - y - 1;
                newX = chunkSize - newX - 1;
                var newY = x;

                newX += chunkOffsetX;
                newY += chunkOffsetY;

                var updatedDestinationIndex = newY * width + newX;
                var channel = reader.ReadUInt16();

                if (updatedDestinationIndex >= 0 && updatedDestinationIndex < textureBufferSize)
                {
                    textureBuffer[updatedDestinationIndex] = channel;
                }
            }
        }

        return textureBuffer;
    }

    /// <summary>
    /// Decodes all mip levels of a VQ mipmapped texture (format 0x400).
    /// Codebook at textureOffset+0..0x800, indices at textureOffset+0x800.
    /// Index layout: [1-byte sentinel][1×1 blocks][2×2 blocks]...[main]
    /// </summary>
    private static PvrMipChain DecodeTwiddledVqAllLevels(BinaryReader reader, PsxTextureHeader header)
    {
        var textureOffset = reader.BaseStream.Position;
        var levels = new List<ushort[]>();

        // Pre-read the codebook (256 entries, 4 pixels each)
        var codebook = ReadVqCodebook(reader, textureOffset);

        // VQ operates on 2×2 blocks. Track cumulative index offset in the index region.
        var indexOffset = 1; // Skip 1-byte sentinel

        // Decode mip levels from smallest (2×2 pixels = 1 block) upward
        var pixelDim = 2;
        while (pixelDim <= header.Width)
        {
            var blockDim = pixelDim / 2;
            var level = DecodeVqSurface(reader, textureOffset, codebook, pixelDim, blockDim, indexOffset);
            levels.Add(level);
            indexOffset += blockDim * blockDim;
            pixelDim *= 2;
        }

        // Reverse to DDS convention (largest first)
        levels.Reverse();

        return new PvrMipChain
        {
            Levels = levels,
            Width = header.Width,
            Height = header.Height
        };
    }

    /// <summary>
    /// Reads the VQ codebook: 256 entries of 4 ushort pixels each.
    /// </summary>
    private static ushort[][] ReadVqCodebook(BinaryReader reader, long textureOffset)
    {
        reader.BaseStream.Seek(textureOffset, SeekOrigin.Begin);
        var codebook = new ushort[256][];
        for (var i = 0; i < 256; i++)
        {
            codebook[i] = new ushort[4];
            for (var j = 0; j < 4; j++)
                codebook[i][j] = reader.ReadUInt16();
        }
        return codebook;
    }

    /// <summary>
    /// Decodes a single VQ surface at the given index offset within the index region.
    /// </summary>
    private static ushort[] DecodeVqSurface(BinaryReader reader, long textureOffset,
        ushort[][] codebook, int pixelDim, int blockDim, int indexStartOffset)
    {
        var textureBuffer = new ushort[pixelDim * pixelDim];
        Array.Fill(textureBuffer, (ushort)0xFF);

        for (var row = 0; row < blockDim; row++)
        {
            for (var col = 0; col < blockDim; col++)
            {
                var colorOffset = MortonCurve.Interleave(row, col) + indexStartOffset;

                // Read the index byte from the index region (at textureOffset + 0x800)
                reader.BaseStream.Seek(textureOffset + 0x800 + colorOffset, SeekOrigin.Begin);
                var paletteIndex = reader.ReadByte();

                var colorBlock = codebook[paletteIndex];

                var baseIndex = row * pixelDim * 2 + col * 2;
                if (baseIndex < textureBuffer.Length)
                    textureBuffer[baseIndex] = colorBlock[0];
                if (baseIndex + 1 < textureBuffer.Length)
                    textureBuffer[baseIndex + 1] = colorBlock[2];
                if (baseIndex + pixelDim < textureBuffer.Length)
                    textureBuffer[baseIndex + pixelDim] = colorBlock[1];
                if (baseIndex + pixelDim + 1 < textureBuffer.Length)
                    textureBuffer[baseIndex + pixelDim + 1] = colorBlock[3];
            }
        }

        return textureBuffer;
    }
}
