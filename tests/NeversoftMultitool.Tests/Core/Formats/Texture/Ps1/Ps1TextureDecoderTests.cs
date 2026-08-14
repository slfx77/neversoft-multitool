using NeversoftMultitool.Core.Formats.Texture.Ps1;
using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps1;

public sealed class Ps1TextureDecoderTests
{
    private const uint TexId = 0x12345678;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-2, -1)]
    public void Extract4BitTexture_NonpositiveDimensions_ReturnNull(int width, int height)
    {
        using var stream = new MemoryStream(new byte[4], false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract4BitTexture(
            reader,
            CreateHeader(16, width, height),
            [CreatePalette(16)]);

        Assert.Null(pixels);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Extract4BitTexture_MissingComputedPadding_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[] { 0x10, 0x00, 0x00 }, false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract4BitTexture(
            reader,
            CreateHeader(16),
            [CreatePalette(16)]);

        Assert.Null(pixels);
    }

    [Fact]
    public void Extract4BitTexture_CompletePayload_DecodesExactRgba()
    {
        using var stream = new MemoryStream(new byte[] { 0x10, 0x00, 0x00, 0x00 }, false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract4BitTexture(
            reader,
            CreateHeader(16),
            [CreatePalette(16)]);

        Assert.NotNull(pixels);
        Assert.Equal(
            new byte[]
            {
                255, 0, 0, 255,
                0, 255, 0, 255
            },
            pixels);
    }

    [Fact]
    public void Extract8BitTexture_MissingComputedPadding_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x00 }, false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract8BitTexture(
            reader,
            CreateHeader(256),
            [CreatePalette(256)]);

        Assert.Null(pixels);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-2, -1)]
    public void Extract8BitTexture_NonpositiveDimensions_ReturnNull(int width, int height)
    {
        using var stream = new MemoryStream(new byte[4], false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract8BitTexture(
            reader,
            CreateHeader(256, width, height),
            [CreatePalette(256)]);

        Assert.Null(pixels);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Extract8BitTexture_CompletePayload_DecodesExactRgba()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x00, 0x00 }, false);
        using var reader = new BinaryReader(stream);

        var pixels = Ps1TextureDecoder.Extract8BitTexture(
            reader,
            CreateHeader(256),
            [CreatePalette(256)]);

        Assert.NotNull(pixels);
        Assert.Equal(
            new byte[]
            {
                255, 0, 0, 255,
                0, 255, 0, 255
            },
            pixels);
    }

    private static PsxTextureHeader CreateHeader(uint paletteSize, int width = 2, int height = 1)
    {
        return new PsxTextureHeader
        {
            PalSize = paletteSize,
            TexId = TexId,
            Width = width,
            Height = height
        };
    }

    private static PsxPalette CreatePalette(int colorCount)
    {
        var colors = new ushort[colorCount];
        colors[0] = 0x001F;
        colors[1] = 0x03E0;
        return new PsxPalette
        {
            TexId = TexId,
            ColorData = colors
        };
    }
}
