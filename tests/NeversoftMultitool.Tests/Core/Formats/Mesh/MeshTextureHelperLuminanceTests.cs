using NeversoftMultitool.Core.Formats.Mesh;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh;

public class MeshTextureHelperLuminanceTests
{
    [Fact]
    public void ConvertLuminanceToAlpha_OpaqueWhiteRemainsFullyOpaque()
    {
        using var source = new Image<Rgba32>(1, 1);
        source[0, 0] = new Rgba32(255, 255, 255, 255);
        using var encoded = new MemoryStream();
        source.SaveAsPng(encoded);

        var result = MeshTextureHelper.ConvertLuminanceToAlpha(encoded.ToArray());

        using var converted = Image.Load<Rgba32>(result);
        Assert.Equal(new Rgba32(255, 255, 255, 255), converted[0, 0]);
    }
}
