using NeversoftMultitool.Core.Formats.GsDump;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsRenderTargetCacheTests
{
    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16)]
    [InlineData(Ps2GsVram.PSMCT16S)]
    public void TryComposeSample_Psmct16Family_Uses64PixelPageRows(uint psm)
    {
        const uint sampleTbp = 0;
        const uint fbw = 2;
        const uint surfaceFbp = 64; // Page 2: row 1 at FBW=2.
        const int surfaceX = 3;
        const int surfaceY = 4;

        // Use the GS VRAM addressor as the placement oracle. In a two-page-wide
        // PSMCT16/16S buffer, FBP=64 maps local (3,4) to sample (3,68): each
        // 8 KiB page is 64x64, not the 64x32 geometry used by PSMCT32/24.
        var vram = new Ps2GsVram();
        vram.WritePixel(surfaceFbp, fbw, psm, surfaceX, surfaceY,
            0xE8, 0x78, 0x28, 0xFF);
        var expected = vram.ReadPixelRgba(sampleTbp, fbw, psm, 3, 68);
        Assert.NotEqual((0, 0, 0, 0), expected);

        var cache = new GsRenderTargetCache();
        cache.WritePixel(surfaceFbp, fbw, psm, surfaceX, surfaceY,
            expected.R, expected.G, expected.B, expected.A);

        var composed = Assert.IsType<byte[]>(cache.TryComposeSample(
            sampleTbp, fbw, 128, 128, psm));

        Assert.Equal(expected, ReadPixel(composed, 128, 3, 68));
        Assert.Equal((0, 0, 0, 0), ReadPixel(composed, 128, 3, 36));
    }

    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, Ps2GsVram.PSMCT16S)]
    [InlineData(Ps2GsVram.PSMCT16S, Ps2TexPixelDecoder.PSMCT16)]
    public void TryComposeSample_Psmct16Family_DoesNotCrossDistinctBlockLayouts(
        uint texturePsm, uint surfacePsm)
    {
        var cache = new GsRenderTargetCache();
        cache.WritePixel(0, 1, surfacePsm, 2, 3, 0xF8, 0x40, 0x20, 0xFF);

        Assert.Null(cache.TryComposeSample(0, 1, 64, 64, texturePsm));
    }

    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16)]
    [InlineData(Ps2GsVram.PSMCT16S)]
    public void TryComposeSample_Psmct16Family_PrefersMatchingFbwSurface(uint psm)
    {
        var cache = new GsRenderTargetCache();
        cache.WritePixel(0, 1, psm, 5, 6, 0x20, 0xE0, 0x40, 0xFF);
        cache.WritePixel(0, 2, psm, 5, 6, 0xE0, 0x20, 0x40, 0xFF);

        var composed = Assert.IsType<byte[]>(cache.TryComposeSample(
            0, 2, 128, 64, psm));

        Assert.Equal((0xE0, 0x20, 0x40, 0xFF), ReadPixel(composed, 128, 5, 6));
    }

    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16)]
    [InlineData(Ps2GsVram.PSMCT16S)]
    public void TryComposeSample_Psmct16Family_ComposesPartialPageWithZeroTbw(uint psm)
    {
        var cache = new GsRenderTargetCache();
        cache.WritePixel(0, 1, psm, 7, 8, 0xD0, 0x60, 0x28, 0xFF);

        var composed = Assert.IsType<byte[]>(cache.TryComposeSample(
            0, 0, 32, 32, psm));

        Assert.Equal((0xD0, 0x60, 0x28, 0xFF), ReadPixel(composed, 32, 7, 8));
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(byte[] rgba, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
    }
}
