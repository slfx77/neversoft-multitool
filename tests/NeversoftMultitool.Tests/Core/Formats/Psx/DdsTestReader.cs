using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

/// <summary>
///     Minimal DDS reader for test verification.
/// </summary>
internal static class DdsTestReader
{
    public static DdsHeader ReadHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadUInt32();
        Assert.Equal(0x20534444u, magic); // "DDS "

        var headerSize = reader.ReadUInt32();
        Assert.Equal(124u, headerSize);

        var flags = reader.ReadUInt32();
        var height = reader.ReadInt32();
        var width = reader.ReadInt32();
        reader.ReadUInt32(); // pitch
        reader.ReadUInt32(); // depth
        var mipMapCount = reader.ReadUInt32();
        for (var i = 0; i < 11; i++) reader.ReadUInt32(); // reserved

        // Pixel format
        reader.ReadUInt32(); // pfSize
        reader.ReadUInt32(); // pfFlags
        reader.ReadUInt32(); // fourCC
        reader.ReadUInt32(); // rgbBitCount
        var rMask = reader.ReadUInt32();
        reader.ReadUInt32(); // gMask
        reader.ReadUInt32(); // bMask
        reader.ReadUInt32(); // aMask

        var caps = reader.ReadUInt32();

        var format = rMask switch
        {
            0x7C00 => ColorFormat.Argb1555,
            0xF800 => ColorFormat.Rgb565,
            0x0F00 => ColorFormat.Argb4444,
            _ => ColorFormat.Argb1555
        };

        return new DdsHeader(width, height, flags, mipMapCount, caps, format);
    }

    public static DdsSurface ReadMainSurface(string path)
    {
        var header = ReadHeader(path);

        using var stream = File.OpenRead(path);
        stream.Seek(128, SeekOrigin.Begin); // Skip magic (4) + header (124)

        using var reader = new BinaryReader(stream);
        var pixelCount = header.Width * header.Height;
        var pixels = new ushort[pixelCount];
        for (var i = 0; i < pixelCount; i++)
            pixels[i] = reader.ReadUInt16();

        return new DdsSurface(header.Width, header.Height, header.Format, pixels);
    }

    public sealed record DdsHeader(int Width, int Height, uint Flags, uint MipMapCount, uint Caps, ColorFormat Format);

    public sealed record DdsSurface(int Width, int Height, ColorFormat Format, ushort[] Pixels);
}