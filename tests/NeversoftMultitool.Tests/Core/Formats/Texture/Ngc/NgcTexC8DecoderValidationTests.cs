using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexC8DecoderValidationTests
{
    [Fact]
    public void DecodeToRgba_UnrepresentableOutputDimensions_RejectsBeforeIndexing()
    {
        var data = new byte[544];

        var exception = Assert.Throws<InvalidDataException>(
            () => NgcTexC8Decoder.DecodeToRgba(data, 1 << 29, 2));

        Assert.Equal(
            "C8 dimensions 536870912x2 exceed the runtime array limit",
            exception.Message);
    }

    [Fact]
    public void DecodeToRgba_IncompletePaddedTile_ThrowsInvalidDataException()
    {
        var data = new byte[513];

        Assert.Throws<InvalidDataException>(() => NgcTexC8Decoder.DecodeToRgba(data, 1, 1));
    }

    [Theory]
    [InlineData(544)]
    [InlineData(545)]
    public void DecodeToRgba_CompletePaddedTile_DecodesPaletteAfterIndices(int dataLength)
    {
        var data = new byte[dataLength];
        data[0] = 1;
        data[34] = 0xFF;
        data[35] = 0xFF;

        var pixels = NgcTexC8Decoder.DecodeToRgba(data, 1, 1);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], pixels);
    }
}
