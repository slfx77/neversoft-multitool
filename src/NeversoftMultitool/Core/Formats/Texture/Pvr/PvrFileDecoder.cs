using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Core.Formats.Texture.Pvr;

/// <summary>
///     Decodes standalone PVR texture files (GBIX+PVRT container format) to PNG.
///     Bridges the standard Sega Dreamcast PVR container to the existing PvrTextureDecoder.
/// </summary>
public static class PvrFileDecoder
{
    private static readonly byte[] GbixMagic = "GBIX"u8.ToArray();
    private static readonly byte[] PvrtMagic = "PVRT"u8.ToArray();

    /// <summary>
    ///     Decodes a PVR texture to RGBA pixel data for preview.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height)? DecodeToRgba(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return DecodeToRgba(reader, 0);
    }

    /// <summary>In-memory variant of <see cref="DecodeToRgba(string)"/>.</summary>
    public static (byte[] Rgba, int Width, int Height)? DecodeToRgba(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);
        return DecodeToRgba(reader, 0);
    }

    /// <summary>
    ///     Decodes a PVR texture from a stream to RGBA pixel data.
    ///     The reader should be positioned at the start of the GBIX or PVRT header.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height)? DecodeToRgba(BinaryReader reader, long pvrDataOffset)
    {
        var header = ParseHeader(reader, pvrDataOffset);
        if (header == null) return null;

        var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
        if (textureBuffer == null) return null;

        var rgbaPixels = ColorHelpers.Convert16BitTextureToRgba(
            header.PixelFormat, header.Width, header.Height, textureBuffer);
        return (rgbaPixels, header.Width, header.Height);
    }

    /// <summary>
    ///     Decodes PVR bytes and writes the result to a PNG file. Used by archive-sourced
    ///     PVR entries where no filesystem path exists.
    /// </summary>
    public static bool DecodeToPng(byte[] data, string pngPath)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);
        return DecodeToPng(reader, 0, pngPath);
    }

    /// <summary>
    ///     Decodes a PVR texture from a stream and writes it as a PNG file.
    ///     The reader should be positioned at the start of the GBIX or PVRT header.
    /// </summary>
    public static bool DecodeToPng(BinaryReader reader, long pvrDataOffset, string pngPath)
    {
        var header = ParseHeader(reader, pvrDataOffset);
        if (header == null) return false;

        var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
        if (textureBuffer == null) return false;

        var rgbaPixels = ColorHelpers.Convert16BitTextureToRgba(
            header.PixelFormat, header.Width, header.Height, textureBuffer);
        ImageWriter.WritePng(pngPath, header.Width, header.Height, rgbaPixels);

        return true;
    }

    private static PsxTextureHeader? ParseHeader(BinaryReader reader, long pvrDataOffset)
    {
        reader.BaseStream.Seek(pvrDataOffset, SeekOrigin.Begin);

        // Check for GBIX header (optional — some PVR files start directly with PVRT)
        var magic = reader.ReadBytes(4);
        if (magic.AsSpan().SequenceEqual(GbixMagic))
        {
            var gbixDataSize = reader.ReadUInt32();
            reader.BaseStream.Seek(gbixDataSize, SeekOrigin.Current);
            magic = reader.ReadBytes(4);
        }

        // Validate PVRT magic
        if (!magic.AsSpan().SequenceEqual(PvrtMagic))
            return null;

        // Parse PVRT header
        var pvrtDataSize = reader.ReadUInt32();
        var pixelFormat = reader.ReadByte();
        var dataType = reader.ReadByte();
        reader.ReadUInt16(); // padding
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();

        // Pixel data starts here
        var textureDataOffset = reader.BaseStream.Position;
        var textureDataSize = pvrtDataSize - 8; // subtract the 8 metadata bytes

        return new PsxTextureHeader
        {
            PalSize = 65536,
            PixelFormat = (uint)(dataType << 8) | pixelFormat,
            Width = width,
            Height = height,
            Size = textureDataSize,
            TextureOffset = textureDataOffset
        };
    }
}
