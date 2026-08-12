using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public class Ps2GsVramPsmt4OddPixelTests
{
    [Fact]
    public void ReadTexturePsmt4_OneByOne_ReturnsSinglePackedByte()
    {
        var vram = new Ps2GsVram();
        vram.WriteRect(0, 1, Ps2GsVram.PSMT4, 1, 1, [0x0B]);

        var decoded = vram.ReadTexturePSMT4(0, 1, 1, 1);

        Assert.Equal(new byte[] { 0x0B }, decoded);
    }

    [Fact]
    public void ReadTexturePsmt4_TwoByOne_PreservesPackedByte()
    {
        var vram = new Ps2GsVram();
        vram.WriteRect(0, 1, Ps2GsVram.PSMT4, 2, 1, [0xBA]);

        var decoded = vram.ReadTexturePSMT4(0, 1, 2, 1);

        Assert.Equal(new byte[] { 0xBA }, decoded);
    }
}
