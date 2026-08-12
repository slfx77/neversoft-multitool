using NeversoftMultitool.Core.Formats.Texture;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Pvr;

public sealed class PvrRgb565ColorExpansionTests
{
    [Fact]
    public void Convert16BitTextureToRgba_Rgb565ReplicatesEndpointBits()
    {
        var rgba = ColorHelpers.Convert16BitTextureToRgba(
            pixelFormat: 0x01,
            width: 1,
            height: 1,
            textureBuffer: [0x2204]);

        Assert.Equal(new byte[] { 0x21, 0x41, 0x21, 0xFF }, rgba);
    }
}
