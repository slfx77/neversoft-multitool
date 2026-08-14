using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexCmprDecoderValidationTests
{
    [Fact]
    public void DecodeToRgba_UnrepresentableOutput_ThrowsInvalidData()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => NgcTexCmprDecoder.DecodeToRgba(new byte[32], 1 << 29, 2));

        Assert.Equal(
            $"CMPR dimensions {1 << 29}x2 exceed the runtime array limit",
            exception.Message);
    }

    [Fact]
    public void DecodeToRgba_TruncatedTwoTileRow_ThrowsInvalidData()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => NgcTexCmprDecoder.DecodeToRgba(new byte[63], 9, 1));

        Assert.Equal("CMPR data too small: expected at least 64 bytes, found 63.", exception.Message);
    }

    [Fact]
    public void DecodeToRgba_ExactTwoTileRow_ReturnsCroppedSurface()
    {
        var pixels = NgcTexCmprDecoder.DecodeToRgba(new byte[64], 9, 1);

        Assert.Equal(9 * 4, pixels.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels.AsSpan(offset, 4).ToArray());
    }
}
