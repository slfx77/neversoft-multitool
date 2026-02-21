using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public class PvrMipChainTests
{
    [Fact]
    public void ToAtlasRgba_CorrectDimensions()
    {
        // 4x4 main + 2x2 + 1x1 mip chain
        var chain = new PvrMipChain
        {
            Width = 4,
            Height = 4,
            Levels =
            [
                new ushort[4 * 4], // 4x4 main
                new ushort[2 * 2], // 2x2
                new ushort[1 * 1]  // 1x1
            ]
        };

        var (rgba, atlasW, atlasH) = chain.ToAtlasRgba(0x100); // ARGB1555 twiddled

        // Atlas width = 4 + 4/2 = 6, height = 4
        Assert.Equal(6, atlasW);
        Assert.Equal(4, atlasH);
        Assert.Equal(6 * 4 * 4, rgba.Length); // width * height * 4 bytes
    }

    [Fact]
    public void ToAtlasRgba_MainSurfacePlacedAtOrigin()
    {
        // Create a 2x2 main surface with known pixel values
        // ARGB1555: 0xFC00 = A=1 R=31 G=0 B=0 (opaque red)
        var redPixel = (ushort)0xFC00;
        var chain = new PvrMipChain
        {
            Width = 2,
            Height = 2,
            Levels =
            [
                [redPixel, redPixel, redPixel, redPixel], // 2x2 red
                [0]  // 1x1 black
            ]
        };

        var (rgba, _, _) = chain.ToAtlasRgba(0x100);

        // Check top-left pixel of atlas (0,0) — should be red
        Assert.Equal(255, rgba[0]); // R
        Assert.Equal(0, rgba[1]);   // G
        Assert.Equal(0, rgba[2]);   // B
        Assert.Equal(255, rgba[3]); // A

        // Check pixel at (1,0) — still main surface, should be red
        var offset = 1 * 4;
        Assert.Equal(255, rgba[offset]);     // R
        Assert.Equal(255, rgba[offset + 3]); // A

        // Check pixel at (2,0) — right side, should be 1x1 mip (black/transparent)
        var mipOffset = 2 * 4;
        Assert.Equal(0, rgba[mipOffset]);     // R
        Assert.Equal(0, rgba[mipOffset + 1]); // G
        Assert.Equal(0, rgba[mipOffset + 2]); // B
    }

    [Fact]
    public void ToAtlasRgba_SingleLevel_StillWorks()
    {
        var chain = new PvrMipChain
        {
            Width = 2,
            Height = 2,
            Levels = [new ushort[4]]
        };

        var (rgba, atlasW, atlasH) = chain.ToAtlasRgba(0x100);

        Assert.Equal(3, atlasW); // 2 + 1
        Assert.Equal(2, atlasH);
        Assert.Equal(3 * 2 * 4, rgba.Length);
    }
}
