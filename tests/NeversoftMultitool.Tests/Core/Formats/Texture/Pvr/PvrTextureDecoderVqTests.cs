using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Pvr;

public sealed class PvrTextureDecoderVqTests
{
    [Fact]
    public void Extract16BitTexture_Vq4By4_PlacesMortonBlocksByColumnThenRow()
    {
        var data = CreateVqData(0, 1, 2, 3);
        var header = CreateHeader(0x300, data.Length);
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);

        var pixels = PvrTextureDecoder.Extract16BitTexture(reader, header);

        Assert.NotNull(pixels);
        Assert.Equal(
        [
            0x8001, 0x8001, 0x8002, 0x8002,
            0x8001, 0x8001, 0x8002, 0x8002,
            0x8003, 0x8003, 0x8004, 0x8004,
            0x8003, 0x8003, 0x8004, 0x8004
        ], pixels);
    }

    [Fact]
    public void Extract16BitTextureWithMips_Vq4By4_PlacesMainAndLowerLevelsCorrectly()
    {
        // VQ mip indices begin with a sentinel, followed by the 2x2 level and
        // then the four Morton-ordered blocks of the 4x4 main surface.
        var data = CreateVqData(0xFF, 4, 0, 1, 2, 3);
        var header = CreateHeader(0x400, data.Length);
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);

        var mipChain = PvrTextureDecoder.Extract16BitTextureWithMips(reader, header);

        Assert.NotNull(mipChain);
        Assert.Equal(4, mipChain.Width);
        Assert.Equal(4, mipChain.Height);
        Assert.Equal(2, mipChain.MipCount);
        Assert.Equal(
        [
            0x8001, 0x8001, 0x8002, 0x8002,
            0x8001, 0x8001, 0x8002, 0x8002,
            0x8003, 0x8003, 0x8004, 0x8004,
            0x8003, 0x8003, 0x8004, 0x8004
        ], mipChain.Levels[0]);
        Assert.Equal([0x8005, 0x8005, 0x8005, 0x8005], mipChain.Levels[1]);
    }

    private static PsxTextureHeader CreateHeader(uint pixelFormat, int dataLength)
    {
        return new PsxTextureHeader
        {
            Width = 4,
            Height = 4,
            PixelFormat = pixelFormat,
            TextureOffset = 0,
            Size = (uint)dataLength
        };
    }

    private static byte[] CreateVqData(params byte[] indices)
    {
        const int codebookSize = 0x800;
        var data = new byte[codebookSize + indices.Length];

        for (var entry = 0; entry < 5; entry++)
        {
            var color = (ushort)(0x8001 + entry);
            for (var pixel = 0; pixel < 4; pixel++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    data.AsSpan(entry * 8 + pixel * sizeof(ushort)),
                    color);
            }
        }

        indices.CopyTo(data, codebookSize);
        return data;
    }
}
