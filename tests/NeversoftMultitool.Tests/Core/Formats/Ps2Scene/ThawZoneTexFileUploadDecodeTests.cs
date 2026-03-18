using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawZoneTexFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawZoneTexFileUploadDecodeTests
{
    [Fact]
    public void DecodeFromTex0Values_PrefersFirstPaletteSnapshotOverFinalOverwrite()
    {
        const uint tbp = 0x2200;
        const uint cbp = 0x3200;

        var uploads = new List<ThawZoneTexFile.VramUpload>
        {
            new(tbp, 2, Ps2TexPixelDecoder.PSMT8, 16, 16, Enumerable.Repeat((byte)0, 16 * 16).ToArray()),
            new(cbp, 1, Ps2TexPixelDecoder.PSMCT32, 16, 16, BuildCt32Palette(255, 0, 0, 0x80)),
            new(cbp, 1, Ps2TexPixelDecoder.PSMCT32, 16, 16, BuildCt32Palette(0, 255, 0, 0x80))
        };

        var textures = ThawZoneTexFile.DecodeFromTex0Values(
            uploads,
            [BuildTex0(tbp, 2, Ps2TexPixelDecoder.PSMT8, 16, 16, cbp, Ps2TexPixelDecoder.PSMCT32)]);

        var texture = Assert.Single(textures);
        AssertSolidColor(texture, 255, 0, 0, 255);
    }

    [Fact]
    public void DecodeFromTex0Values_WaitsForTextureUploadWhenPaletteArrivesFirst()
    {
        const uint tbp = 0x2201;
        const uint cbp = 0x3201;

        var uploads = new List<ThawZoneTexFile.VramUpload>
        {
            new(cbp, 1, Ps2TexPixelDecoder.PSMCT32, 16, 16, BuildCt32Palette(255, 0, 0, 0x80)),
            new(tbp, 2, Ps2TexPixelDecoder.PSMT8, 16, 16, Enumerable.Repeat((byte)0, 16 * 16).ToArray()),
            new(cbp, 1, Ps2TexPixelDecoder.PSMCT32, 16, 16, BuildCt32Palette(0, 255, 0, 0x80))
        };

        var textures = ThawZoneTexFile.DecodeFromTex0Values(
            uploads,
            [BuildTex0(tbp, 2, Ps2TexPixelDecoder.PSMT8, 16, 16, cbp, Ps2TexPixelDecoder.PSMCT32)]);

        var texture = Assert.Single(textures);
        AssertSolidColor(texture, 255, 0, 0, 255);
    }
}
