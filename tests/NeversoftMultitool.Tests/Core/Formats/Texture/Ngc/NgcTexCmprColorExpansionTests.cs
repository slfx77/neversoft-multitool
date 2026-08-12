using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexCmprColorExpansionTests
{
    [Fact]
    public void DecodeToRgba_ReplicatesRgb565EndpointBits()
    {
        byte[] subBlock = [
            0x22, 0x04, // RGB565 endpoint 0: r5=4, g6=16, b5=4
            0x00, 0x00, // Endpoint 1
            0x00, 0x00, 0x00, 0x00 // Every selector chooses endpoint 0
        ];
        var data = new byte[32];
        for (var offset = 0; offset < data.Length; offset += subBlock.Length)
            subBlock.CopyTo(data, offset);

        var pixels = NgcTexCmprDecoder.DecodeToRgba(data, 4, 4);
        byte[] expectedPixel = [0x21, 0x41, 0x21, 0xFF];

        Assert.Equal(4 * 4 * 4, pixels.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(expectedPixel, pixels.AsSpan(offset, 4).ToArray());
    }
}
