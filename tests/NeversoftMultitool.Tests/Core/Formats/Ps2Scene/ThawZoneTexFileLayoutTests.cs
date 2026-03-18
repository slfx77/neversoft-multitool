using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawZoneTexFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawZoneTexFileLayoutTests
{
    [Fact]
    public void TransformPsmt4SlotBlocks_ReordersMacroblocksByBitPermutation()
    {
        const int width = 64;
        const int height = 32;
        var texData = new byte[width * height / 2];

        FillPsmt4Block(texData, width, 0, 0, 32, 16, 0x1);
        FillPsmt4Block(texData, width, 32, 0, 32, 16, 0x2);
        FillPsmt4Block(texData, width, 0, 16, 32, 16, 0x3);
        FillPsmt4Block(texData, width, 32, 16, 32, 16, 0x4);

        var transformed = ThawZoneTexFile.TransformPsmt4SlotBlocks(texData, width, height, [1, 0], 0);

        Assert.Equal(0x1, ReadPsmt4Index(transformed, width, 0, 0));
        Assert.Equal(0x3, ReadPsmt4Index(transformed, width, 32, 0));
        Assert.Equal(0x2, ReadPsmt4Index(transformed, width, 0, 16));
        Assert.Equal(0x4, ReadPsmt4Index(transformed, width, 32, 16));
    }

    [Fact]
    public void TransformPsmt4LinearBlocks_ReordersTilesByBitPermutation()
    {
        const int width = 32;
        const int height = 32;
        var texData = new byte[width * height / 2];

        FillPsmt4Block(texData, width, 0, 0, 16, 16, 0x1);
        FillPsmt4Block(texData, width, 16, 0, 16, 16, 0x2);
        FillPsmt4Block(texData, width, 0, 16, 16, 16, 0x3);
        FillPsmt4Block(texData, width, 16, 16, 16, 16, 0x4);

        var transformed = ThawZoneTexFile.TransformPsmt4LinearBlocks(texData, width, height, 16, 16, [1, 0], 0);

        Assert.Equal(0x1, ReadPsmt4Index(transformed, width, 0, 0));
        Assert.Equal(0x3, ReadPsmt4Index(transformed, width, 16, 0));
        Assert.Equal(0x2, ReadPsmt4Index(transformed, width, 0, 16));
        Assert.Equal(0x4, ReadPsmt4Index(transformed, width, 16, 16));
    }

    [Fact]
    public void ShouldApplyPsmt4SlotLayoutTransform_RequiresSupportedLayout()
    {
        Assert.True(ThawZoneTexFile.ShouldApplyPsmt4SlotLayoutTransform(0x02000005, 0x20));
        Assert.True(ThawZoneTexFile.ShouldApplyPsmt4SlotLayoutTransform(0x02000001, 0));
        Assert.False(ThawZoneTexFile.ShouldApplyPsmt4SlotLayoutTransform(0x00040001, 0x20));
    }

    [Fact]
    public void ShouldApplyPsmt4SlotTileTransform_Requires02000005Biased64x32Upload()
    {
        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x12345678,
            BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x3590, Ps2TexPixelDecoder.PSMCT32),
            0x2000,
            0x20,
            0x40,
            0x1000,
            0,
            0,
            0x02000005);

        Assert.True(ThawZoneTexFile.ShouldApplyPsmt4SlotTileTransform(
            entry,
            0x20,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 32, new byte[0x2000])));
        Assert.False(ThawZoneTexFile.ShouldApplyPsmt4SlotTileTransform(
            entry,
            0,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 32, new byte[0x2000])));
        Assert.False(ThawZoneTexFile.ShouldApplyPsmt4SlotTileTransform(
            entry,
            0x20,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 16, new byte[0x1000])));
        Assert.False(ThawZoneTexFile.ShouldApplyPsmt4SlotTileTransform(
            entry with { LayoutMode = 0x02000001 },
            0x20,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 32, new byte[0x2000])));
    }

    [Fact]
    public void ShouldPreferPsmt4BiasedAutoSlotCandidate_OnlyMatchesHighOffset02000001BiasZeroBucket()
    {
        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x12345678,
            BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x3590, Ps2TexPixelDecoder.PSMCT16),
            0x2000,
            0x138B20,
            0x20,
            0x1000,
            0,
            0,
            0x02000001);

        var upload = new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 32, new byte[0x2000]);

        Assert.True(ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, 0x20, upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry with { DataOffset = 0x138B00 }, 0x20,
                upload));
        Assert.False(ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, 0, upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry with { LayoutMode = 0x02000005 }, 0x20,
                upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry with { PaletteBytes = 0x40 }, 0x20, upload));
        Assert.False(ThawZoneTexFile.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, 0x20,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 16, new byte[0x1000])));
    }

    [Fact]
    public void ShouldPreferNobiasForBias32Bucket_MatchesHighOffsetBias32Layout02000001()
    {
        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            0x12345678,
            BuildTex0(0x2BC0, 2, Ps2TexPixelDecoder.PSMT4, 128, 128, 0x3590, Ps2TexPixelDecoder.PSMCT16),
            0x2000,
            0x104440,
            0x20,
            0x1000,
            0,
            0,
            0x02000001);

        var upload = new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 32, new byte[0x2000]);

        Assert.True(ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry, 0x20, upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry with { DataOffset = 0x92C40 }, 0x20, upload));
        Assert.False(ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry, 0, upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry with { LayoutMode = 0x02000005 }, 0x20, upload));
        Assert.False(
            ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry with { PaletteBytes = 0x40 }, 0x20, upload));
        Assert.False(ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry with { DataSize = 0x4000 }, 0x20, upload));
        Assert.False(ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry, 0x20,
            new ThawZoneTexFile.VramUpload(0x3000, 1, Ps2TexPixelDecoder.PSMCT32, 64, 16, new byte[0x1000])));
        Assert.False(ThawZoneTexFile.ShouldPreferNobiasForBias32Bucket(entry, 0x20, null));
    }

    [Fact]
    public void ReorderClutPsmt4ForLayout_Layout02000001_Ct16PreservesRawOrder()
    {
        var palette = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();

        var reordered = ThawZoneTexFile.ReorderClutPsmt4ForLayout(
            palette.ToArray(),
            Ps2TexPixelDecoder.PSMCT16,
            0x02000001);

        Assert.Equal(palette, reordered);
    }
}
