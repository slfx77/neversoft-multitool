using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

public sealed class ThawZoneTexHeaderDataSupportTests
{
    [Fact]
    public void DecodeFromHeaderDataSlot_OneByOnePsmt4_InferredPixelBytesUseCeiling()
    {
        AssertOneByOnePsmt4Decodes(basePixelBytes: 0);
    }

    [Fact]
    public void DecodeFromHeaderDataSlot_OneByOnePsmt4_ExplicitPixelBytesStillDecode()
    {
        AssertOneByOnePsmt4Decodes(basePixelBytes: 1);
    }

    private static void AssertOneByOnePsmt4Decodes(uint basePixelBytes)
    {
        var data = new byte[65];
        data[4] = 10;
        data[5] = 20;
        data[6] = 30;
        data[7] = 128;
        data[64] = 0x01;

        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            Checksum: 0x12345678,
            Tex0: (ulong)Ps2TexPixelDecoder.PSMT4 << 20,
            DataSize: 1,
            DataOffset: 0,
            PaletteBytes: 64,
            UploadOffset: 0,
            BasePixelBytes: basePixelBytes);

        var texture = ThawZoneTexHeaderDataSupport.DecodeFromHeaderDataSlot(
            data,
            dataBaseOffset: 0,
            dataOffsetBias: 0,
            entry);

        Assert.NotNull(texture);
        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal([10, 20, 30, 255], texture.Pixels);
    }
}
