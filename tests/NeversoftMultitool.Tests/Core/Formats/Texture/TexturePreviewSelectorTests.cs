using NeversoftMultitool.Core.Formats.Texture;

namespace NeversoftMultitool.Tests.Core.Formats.Texture;

public class TexturePreviewSelectorTests
{
    [Fact]
    public void Select_UsesRawOrdinal_WhenChecksumsRepeatAroundUndecodedTexture()
    {
        byte[] red = [255, 0, 0, 255];
        byte[] blue = [0, 0, 255, 255];
        var result = new Ps2TexResult(
        [
            new Ps2Texture(0x12345678, 1, 1, 0, 0, red),
            new Ps2Texture(0x87654321, 1, 1, 0, 0, null),
            new Ps2Texture(0x12345678, 1, 1, 0, 0, blue)
        ]);

        var first = TexturePreviewSelector.Select(result, 0);
        var third = TexturePreviewSelector.Select(result, 2);

        Assert.NotNull(first);
        Assert.NotNull(third);
        Assert.Same(red, first.Value.Rgba);
        Assert.Same(blue, third.Value.Rgba);
        Assert.Null(TexturePreviewSelector.Select(result, 1));
        Assert.Null(TexturePreviewSelector.Select(result, -1));
        Assert.Null(TexturePreviewSelector.Select(result, 3));
    }

    [Theory]
    [InlineData(2, 2, 4)]
    [InlineData(1, 1, 8)]
    [InlineData(1, 1, 3)]
    public void Select_NonExactRgbaBuffer_ReturnsNull(int width, int height, int byteCount)
    {
        var result = new Ps2TexResult(
        [
            new Ps2Texture(0x12345678, width, height, 0, 0, new byte[byteCount])
        ]);

        Assert.Null(TexturePreviewSelector.Select(result, 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Select_NonPositiveDimension_ReturnsNull(int width, int height)
    {
        var result = new Ps2TexResult(
        [
            new Ps2Texture(0x12345678, width, height, 0, 0, new byte[4])
        ]);

        Assert.Null(TexturePreviewSelector.Select(result, 0));
    }

    [Fact]
    public void Select_UnrepresentableRgbaSurface_ReturnsNull()
    {
        var result = new Ps2TexResult(
        [
            new Ps2Texture(
                0x12345678,
                int.MaxValue,
                int.MaxValue,
                0,
                0,
                new byte[4])
        ]);

        Assert.Null(TexturePreviewSelector.Select(result, 0));
    }

    [Fact]
    public void Select_ExactTwoByTwoRgbaSurface_ReturnsOriginalBufferAndDimensions()
    {
        var pixels = new byte[2 * 2 * 4];
        var result = new Ps2TexResult(
        [
            new Ps2Texture(0x12345678, 2, 2, 0, 0, pixels)
        ]);

        var selected = TexturePreviewSelector.Select(result, 0);

        Assert.NotNull(selected);
        Assert.Same(pixels, selected.Value.Rgba);
        Assert.Equal(2, selected.Value.Width);
        Assert.Equal(2, selected.Value.Height);
    }
}
