using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

public class ThawZoneTexVramTransferSizeTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(4, 1, 2)]
    public void GetTransferSizeBytes_Psmt4UsesCeilingPackedByteCount(
        int width,
        int height,
        int expectedBytes)
    {
        var actual = ThawZoneTexVramSupport.GetTransferSizeBytes(
            Ps2TexPixelDecoder.PSMT4,
            width,
            height);

        Assert.Equal(expectedBytes, actual);
    }
}
