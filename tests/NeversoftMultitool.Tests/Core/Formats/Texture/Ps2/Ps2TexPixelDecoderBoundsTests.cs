using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public sealed class Ps2TexPixelDecoderBoundsTests
{
    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 3)]
    [InlineData(Ps2TexPixelDecoder.PSMCT24, 2)]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 1)]
    [InlineData(Ps2TexPixelDecoder.PSMT8, 0)]
    [InlineData(Ps2TexPixelDecoder.PSMT4, 0)]
    public void DecodePixels_TruncatedSource_ReturnsNull(uint psm, int sourceLength)
    {
        var clut = Ps2TexPixelDecoder.GetPaletteSize(psm) > 0
            ? new byte[Ps2TexPixelDecoder.GetPaletteSize(psm) * 4]
            : null;

        var pixels = Ps2TexPixelDecoder.DecodePixels(
            new byte[sourceLength], 1, 1, psm, Ps2TexPixelDecoder.PSMCT32, clut);

        Assert.Null(pixels);
    }

    [Fact]
    public void DecodePixels_ExactSourceLength_DecodesPixel()
    {
        var pixels = Ps2TexPixelDecoder.DecodePixels(
            [0x11, 0x22, 0x33, 0x80],
            1,
            1,
            Ps2TexPixelDecoder.PSMCT32,
            Ps2TexPixelDecoder.PSMCT32,
            null);

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, pixels);
    }

    [Fact]
    public void DecodePixels_OddPsmt4PixelCount_UsesCeilingSourceLength()
    {
        var clut = new byte[16 * 4];
        new byte[] { 0xFF, 0x00, 0x00, 0x80 }.CopyTo(clut, 4);
        new byte[] { 0x00, 0xFF, 0x00, 0x80 }.CopyTo(clut, 8);
        new byte[] { 0x00, 0x00, 0xFF, 0x80 }.CopyTo(clut, 12);

        var pixels = Ps2TexPixelDecoder.DecodePixels(
            [0x21, 0x03],
            3,
            1,
            Ps2TexPixelDecoder.PSMT4,
            Ps2TexPixelDecoder.PSMCT32,
            clut);

        Assert.Equal(
            new byte[]
            {
                0xFF, 0x00, 0x00, 0xFF,
                0x00, 0xFF, 0x00, 0xFF,
                0x00, 0x00, 0xFF, 0xFF
            },
            pixels);
    }

    [Fact]
    public void DecodePixels_OutputLargerThanRuntimeArrayLimit_ReturnsNull()
    {
        var pixels = Ps2TexPixelDecoder.DecodePixels(
            [],
            int.MaxValue,
            1,
            Ps2TexPixelDecoder.PSMCT32,
            Ps2TexPixelDecoder.PSMCT32,
            null);

        Assert.Null(pixels);
    }
}
