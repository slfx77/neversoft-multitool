using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Pvr;

public sealed class PvrTextureDecoderBoundsTests
{
    [Fact]
    public void Extract16BitTexture_RectangleCrossingDeclaredSize_ReturnsNull()
    {
        using var stream = new MemoryStream([0x11, 0x11, 0x22, 0x22], writable: false);
        using var reader = new BinaryReader(stream);

        var pixels = PvrTextureDecoder.Extract16BitTexture(
            reader,
            CreateRectangleHeader(size: 2));

        Assert.Null(pixels);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void Extract16BitTexture_RectangleWithinDeclaredSize_DecodesAllPixels()
    {
        using var stream = new MemoryStream([0x11, 0x11, 0x22, 0x22], writable: false);
        using var reader = new BinaryReader(stream);

        var pixels = PvrTextureDecoder.Extract16BitTexture(
            reader,
            CreateRectangleHeader(size: 4));

        Assert.Equal([0x1111, 0x2222], pixels);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void Extract16BitTexture_PhysicallyTruncatedRectangle_ReturnsNull()
    {
        using var stream = new MemoryStream([0x11, 0x11], writable: false);
        using var reader = new BinaryReader(stream);

        var pixels = PvrTextureDecoder.Extract16BitTexture(
            reader,
            CreateRectangleHeader(size: 4));

        Assert.Null(pixels);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void Extract16BitTexture_RectangleAllowsDeclaredPadding()
    {
        using var stream = new MemoryStream(
            [0x11, 0x11, 0x22, 0x22, 0x00, 0x00],
            writable: false);
        using var reader = new BinaryReader(stream);

        var pixels = PvrTextureDecoder.Extract16BitTexture(
            reader,
            CreateRectangleHeader(size: 6));

        Assert.Equal([0x1111, 0x2222], pixels);
        Assert.Equal(6, stream.Position);
    }

    [Fact]
    public void Extract16BitTexture_NegativeRectangleDimensions_ReturnsNull()
    {
        using var stream = new MemoryStream([0x11, 0x11, 0x22, 0x22], writable: false);
        using var reader = new BinaryReader(stream);
        var header = CreateRectangleHeader(size: 4);
        header.Width = -1;
        header.Height = -2;

        var pixels = PvrTextureDecoder.Extract16BitTexture(reader, header);

        Assert.Null(pixels);
        Assert.Equal(4, stream.Position);
    }

    private static PsxTextureHeader CreateRectangleHeader(uint size)
    {
        return new PsxTextureHeader
        {
            Width = 1,
            Height = 2,
            PixelFormat = 0x900,
            TextureOffset = 0,
            Size = size
        };
    }
}
