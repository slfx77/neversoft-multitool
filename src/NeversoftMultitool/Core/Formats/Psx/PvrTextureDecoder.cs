namespace NeversoftMultitool.Core.Formats.Psx;

public static class PvrTextureDecoder
{
    private static readonly HashSet<int> SupportedFormats = [0x100, 0x200, 0x300, 0x400, 0x900, 0xD00];

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
}
