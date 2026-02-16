namespace NeversoftMultitool.Core.Formats.Psx;

public static class Ps1TextureDecoder
{
    /// <summary>
    /// Extracts a 4-bit (16-color) texture from a PSX file.
    /// </summary>
    public static byte[]? Extract4BitTexture(BinaryReader reader, PsxTextureHeader header,
        List<PsxPalette> palette4Bit)
    {
        var padWidth = (header.Width + 0x3) & ~0x3;
        padWidth >>= 1;
        var realLen = padWidth * header.Height + GetPaddingAmount(header, padWidth);
        var palIndices = reader.ReadBytes(realLen);

        // Find matching palette
        foreach (var pal in palette4Bit)
        {
            if (pal.TexId != header.Hash) continue;

            var pixels = new byte[header.Width * header.Height * 4];
            Span<byte> rgba = stackalloc byte[4];

            for (var y = 0; y < header.Height; y++)
            {
                for (var x = 0; x < header.Width; x++)
                {
                    var byteIndex = y * padWidth + (x >> 1);
                    var colorIndex = (palIndices[byteIndex] >> ((x & 0x1) * 4)) & 0xF;
                    var color = pal.ColorData[colorIndex];

                    ColorHelpers.Ps1To32Bpp(color, rgba);

                    // Note: Python uses pixels[y * width - x] which wraps around
                    var pixelIndex = y * header.Width - x;
                    if (pixelIndex < 0) pixelIndex += header.Width * header.Height;
                    var offset = pixelIndex * 4;

                    pixels[offset] = rgba[0];
                    pixels[offset + 1] = rgba[1];
                    pixels[offset + 2] = rgba[2];
                    pixels[offset + 3] = rgba[3];
                }
            }

            return pixels;
        }

        return null;
    }

    /// <summary>
    /// Extracts an 8-bit (256-color) texture from a PSX file.
    /// </summary>
    public static byte[]? Extract8BitTexture(BinaryReader reader, PsxTextureHeader header,
        List<PsxPalette> palette8Bit)
    {
        var padWidth = (header.Width + 0x1) & ~0x1;
        var realLen = padWidth * header.Height + GetPaddingAmount(header, padWidth);
        var palIndices = reader.ReadBytes(realLen);

        // Find matching palette
        foreach (var pal in palette8Bit)
        {
            if (pal.TexId != header.Hash) continue;

            var pixels = new byte[header.Width * header.Height * 4];
            Span<byte> rgba = stackalloc byte[4];

            for (var y = 0; y < header.Height; y++)
            {
                for (var x = 0; x < header.Width; x++)
                {
                    var colorIndex = palIndices[y * padWidth + x] & 0xFF;
                    var color = pal.ColorData[colorIndex];

                    ColorHelpers.Ps1To32Bpp(color, rgba);

                    // Note: Python uses pixels[y * width - x] which wraps around
                    var pixelIndex = y * header.Width - x;
                    if (pixelIndex < 0) pixelIndex += header.Width * header.Height;
                    var offset = pixelIndex * 4;

                    pixels[offset] = rgba[0];
                    pixels[offset + 1] = rgba[1];
                    pixels[offset + 2] = rgba[2];
                    pixels[offset + 3] = rgba[3];
                }
            }

            return pixels;
        }

        return null;
    }

    private static int GetPaddingAmount(PsxTextureHeader header, int padWidth)
    {
        if (header.Height % 2 != 0)
        {
            return padWidth % 4 != 0 ? 2 : 0;
        }
        return 0;
    }
}

/// <summary>
/// Represents a color palette entry for PS1 textures.
/// </summary>
public sealed class PsxPalette
{
    public required uint TexId { get; init; }
    public required ushort[] ColorData { get; init; }
}
