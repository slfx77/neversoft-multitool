using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Vid1;

public sealed class Vid1YuvToRgbTests
{
    [Theory]
    [InlineData(0, 1, "width")]
    [InlineData(-1, 1, "width")]
    [InlineData(1, 0, "height")]
    [InlineData(1, -1, "height")]
    [InlineData(-1, -1, "width")]
    public void ConversionEntryPoints_NonpositiveDimensions_Throw(
        int width, int height, string expectedParameter)
    {
        var rgb = new byte[4];
        var bgra = new byte[4];

        var allocatingException = Assert.Throws<ArgumentOutOfRangeException>(
            () => Vid1YuvToRgb.Convert([], [], [], width, height));
        var rgbException = Assert.Throws<ArgumentOutOfRangeException>(
            () => Vid1YuvToRgb.ConvertToRgb([], [], [], width, height, rgb));
        var bgraException = Assert.Throws<ArgumentOutOfRangeException>(
            () => Vid1YuvToRgb.ConvertToBgra([], [], [], width, height, bgra));

        Assert.Equal(expectedParameter, allocatingException.ParamName);
        Assert.Equal(expectedParameter, rgbException.ParamName);
        Assert.Equal(expectedParameter, bgraException.ParamName);
    }

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
