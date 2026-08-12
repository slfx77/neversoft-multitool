using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexRgba8DecoderTests
{
    [Fact]
    public void DecodeToRgba_UnrepresentableOutputDimensions_RejectsBeforeIndexing()
    {
        var data = new byte[64];

        var exception = Assert.Throws<InvalidDataException>(
            () => NgcTexRgba8Decoder.DecodeToRgba(data, 1 << 29, 2));

        Assert.Equal(
            "RGBA8 dimensions 536870912x2 exceed the runtime array limit",
            exception.Message);
    }

    [Fact]
    public void DecodeToRgba_CompleteTile_DecodesArAndGbPlanes()
    {
        var data = new byte[64];

        // First texel: AR in the first plane, GB in the second.
        data[0] = 0x44;
        data[1] = 0x11;
        data[32] = 0x22;
        data[33] = 0x33;

        // Last texel in the 4x4 tile exercises the far end of both planes.
        data[30] = 0x88;
        data[31] = 0x55;
        data[62] = 0x66;
        data[63] = 0x77;

        var pixels = NgcTexRgba8Decoder.DecodeToRgba(data, 4, 4);

        Assert.Equal(64, pixels.Length);
        Assert.Equal([0x11, 0x22, 0x33, 0x44], pixels[..4]);
        Assert.Equal([0x55, 0x66, 0x77, 0x88], pixels[^4..]);
    }
}
