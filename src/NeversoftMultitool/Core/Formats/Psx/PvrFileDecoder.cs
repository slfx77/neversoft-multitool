using System.Text;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Decodes standalone PVR texture files (GBIX+PVRT container format) to PNG.
/// Bridges the standard Sega Dreamcast PVR container to the existing PvrTextureDecoder.
/// </summary>
public static class PvrFileDecoder
{
    private static readonly byte[] GbixMagic = "GBIX"u8.ToArray();
    private static readonly byte[] PvrtMagic = "PVRT"u8.ToArray();

    /// <summary>
    /// Decodes a PVR texture from a stream and writes it as a PNG file.
    /// The reader should be positioned at the start of the GBIX or PVRT header.
    /// </summary>
    public static bool DecodeToPng(BinaryReader reader, long pvrDataOffset, string pngPath)
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
            return false;

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

        // Construct a PsxTextureHeader for the existing decoder
        var header = new PsxTextureHeader
        {
            PalSize = 65536,
            PixelFormat = (uint)(dataType << 8) | pixelFormat,
            Width = width,
            Height = height,
            Size = textureDataSize,
            TextureOffset = textureDataOffset
        };

        // Decode using existing PVR decoder
        var textureBuffer = PvrTextureDecoder.Extract16BitTexture(reader, header);
        if (textureBuffer == null)
            return false;

        // Convert to RGBA and write PNG
        var rgbaPixels = ColorHelpers.Convert16BitTextureToRgba(
            header.PixelFormat, width, height, textureBuffer);
        ImageWriter.WritePng(pngPath, width, height, rgbaPixels);

        return true;
    }
}
