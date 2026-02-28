using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core;

/// <summary>
///     Writes 16-bit pixel data to DDS (DirectDraw Surface) files.
/// </summary>
public static class DdsWriter
{
    private const uint DdsMagic = 0x20534444; // "DDS "
    private const uint HeaderSize = 124;
    private const uint PixelFormatSize = 32;

    // DDS_HEADER flags
    private const uint DdsdCaps = 0x1;
    private const uint DdsdHeight = 0x2;
    private const uint DdsdWidth = 0x4;
    private const uint DdsdPitch = 0x8;
    private const uint DdsdPixelFormat = 0x1000;
    private const uint DdsdMipmapCount = 0x20000;
    private const uint DefaultFlags = DdsdCaps | DdsdHeight | DdsdWidth | DdsdPitch | DdsdPixelFormat;

    // DDS_PIXELFORMAT flags
    private const uint DdpfRgb = 0x40;
    private const uint DdpfAlphaPixels = 0x1;

    // DDS_HEADER caps
    private const uint DdsCapsTexture = 0x1000;
    private const uint DdsCapsComplex = 0x8;
    private const uint DdsCapsMipmap = 0x400000;

    /// <summary>
    ///     Writes raw 16-bit pixel data to a DDS file (single surface, no mipmaps).
    /// </summary>
    public static void WriteDds(string outputPath, int width, int height, ColorFormat format, ushort[] pixelData)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        WriteHeader(writer, width, height, format);
        WritePixelData(writer, pixelData);
    }

    /// <summary>
    ///     Writes a DDS file with a complete mip chain.
    ///     Levels in mipChain.Levels are ordered largest to smallest (DDS convention).
    /// </summary>
    public static void WriteDds(string outputPath, ColorFormat format, PvrMipChain mipChain)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        WriteHeader(writer, mipChain.Width, mipChain.Height, format, mipChain.MipCount);

        foreach (var level in mipChain.Levels)
            WritePixelData(writer, level);
    }

    private static void WritePixelData(BinaryWriter writer, ushort[] pixelData)
    {
        var byteData = new byte[pixelData.Length * 2];
        Buffer.BlockCopy(pixelData, 0, byteData, 0, byteData.Length);
        writer.Write(byteData);
    }

    private static void WriteHeader(BinaryWriter writer, int width, int height, ColorFormat format,
        int mipMapCount = 0)
    {
        var (pfFlags, rMask, gMask, bMask, aMask) = GetPixelFormatMasks(format);
        var pitch = width * 2; // 16 bits per pixel = 2 bytes
        var hasMips = mipMapCount > 0;

        // Magic
        writer.Write(DdsMagic);

        // DDS_HEADER
        writer.Write(HeaderSize); // dwSize
        writer.Write(hasMips ? DefaultFlags | DdsdMipmapCount : DefaultFlags); // dwFlags
        writer.Write((uint)height); // dwHeight
        writer.Write((uint)width); // dwWidth
        writer.Write((uint)pitch); // dwPitchOrLinearSize
        writer.Write(0u); // dwDepth
        writer.Write((uint)mipMapCount); // dwMipMapCount
        for (var i = 0; i < 11; i++) // dwReserved1[11]
            writer.Write(0u);

        // DDS_PIXELFORMAT (embedded in header)
        writer.Write(PixelFormatSize); // dwSize
        writer.Write(pfFlags); // dwFlags
        writer.Write(0u); // dwFourCC
        writer.Write(16u); // dwRGBBitCount
        writer.Write(rMask); // dwRBitMask
        writer.Write(gMask); // dwGBitMask
        writer.Write(bMask); // dwBBitMask
        writer.Write(aMask); // dwABitMask

        // Back to DDS_HEADER
        var caps = hasMips
            ? DdsCapsTexture | DdsCapsComplex | DdsCapsMipmap
            : DdsCapsTexture;
        writer.Write(caps); // dwCaps
        writer.Write(0u); // dwCaps2
        writer.Write(0u); // dwCaps3
        writer.Write(0u); // dwCaps4
        writer.Write(0u); // dwReserved2
    }

    private static (uint flags, uint rMask, uint gMask, uint bMask, uint aMask) GetPixelFormatMasks(ColorFormat format)
    {
        return format switch
        {
            ColorFormat.Argb1555 => (DdpfRgb | DdpfAlphaPixels, 0x7C00u, 0x03E0u, 0x001Fu, 0x8000u),
            ColorFormat.Rgb565 => (DdpfRgb, 0xF800u, 0x07E0u, 0x001Fu, 0u),
            ColorFormat.Argb4444 => (DdpfRgb | DdpfAlphaPixels, 0x0F00u, 0x00F0u, 0x000Fu, 0xF000u),
            _ => (DdpfRgb | DdpfAlphaPixels, 0x7C00u, 0x03E0u, 0x001Fu, 0x8000u)
        };
    }
}
