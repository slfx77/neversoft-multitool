using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Vid1;

public sealed class Vid1YuvToRgbTests
{
    [Fact]
    public void FrameContext_OddDimensions_AllocatesCeilingChromaPlanes()
    {
        var context = new Vid1FrameContext(3, 3, new byte[64], new byte[64]);

        Assert.Equal(2, context.ChromaWidth);
        Assert.Equal(2, context.ChromaHeight);
        Assert.Equal(4, context.OutputCb.Length);
        Assert.Equal(4, context.OutputCr.Length);
    }

    [Fact]
    public void Convert_OddDimensions_UsesCeilingChromaRowStride()
    {
        var luma = Enumerable.Repeat((byte)81, 9).ToArray();
        byte[] cb = [90, 240, 54, 128];
        byte[] cr = [240, 110, 34, 128];

        var actual = Vid1YuvToRgb.Convert(luma, cb, cr, 3, 3);

        byte[] expected =
        [
            255, 0, 0, 255, 0, 0, 47, 47, 255,
            255, 0, 0, 255, 0, 0, 47, 47, 255,
            0, 181, 0, 0, 181, 0, 76, 76, 76
        ];
        Assert.Equal(expected, actual);
    }
}
