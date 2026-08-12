using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class DdsWriterTests
{
    private const uint DdsdMipmapCount = 0x20000;
    private const uint DdsCapsTexture = 0x1000;
    private const uint DdsCapsComplex = 0x8;
    private const uint DdsCapsMipmap = 0x400000;

    [Fact]
    public void WriteDds_SingleLevelChain_WritesOrdinaryTextureHeader()
    {
        var path = GetTempPath();
        try
        {
            DdsWriter.WriteDds(path, ColorFormat.Rgb565, new PvrMipChain
            {
                Width = 2,
                Height = 2,
                Levels = [new ushort[4]]
            });

            var (flags, mipMapCount, caps) = ReadMipHeader(path);
            Assert.Equal(0u, flags & DdsdMipmapCount);
            Assert.Equal(0u, mipMapCount);
            Assert.Equal(DdsCapsTexture, caps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteDds_MultipleLevels_WritesMipmappedTextureHeader()
    {
        var path = GetTempPath();
        try
        {
            DdsWriter.WriteDds(path, ColorFormat.Rgb565, new PvrMipChain
            {
                Width = 2,
                Height = 2,
                Levels = [new ushort[4], new ushort[1]]
            });

            var (flags, mipMapCount, caps) = ReadMipHeader(path);
            Assert.Equal(DdsdMipmapCount, flags & DdsdMipmapCount);
            Assert.Equal(2u, mipMapCount);
            Assert.Equal(DdsCapsTexture | DdsCapsComplex | DdsCapsMipmap, caps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string GetTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"nmt-dds-writer-{Guid.NewGuid():N}.dds");
    }

    private static (uint Flags, uint MipMapCount, uint Caps) ReadMipHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        stream.Position = 8;
        var flags = reader.ReadUInt32();
        stream.Position = 28;
        var mipMapCount = reader.ReadUInt32();
        stream.Position = 108;
        var caps = reader.ReadUInt32();
        return (flags, mipMapCount, caps);
    }
}
