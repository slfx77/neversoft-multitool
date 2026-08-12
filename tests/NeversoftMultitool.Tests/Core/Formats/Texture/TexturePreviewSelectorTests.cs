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
}
