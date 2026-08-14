using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class XbxPassCompositorResolutionTests
{
    private const uint BaseTextureChecksum = 0x10203040;
    private const uint OverlayTextureChecksum = 0x50607080;

    [Fact]
    public void CrossedOverlayDimensions_PreserveEveryOverlayColumn()
    {
        var black = new Rgba32(0, 0, 0, 255);
        var basePng = CreatePng(2, 2, [black, black, black, black]);
        Rgba32[] overlayPixels =
        [
            new(255, 0, 0, 255),
            new(0, 255, 0, 255),
            new(0, 0, 255, 255),
            new(255, 255, 255, 255)
        ];
        var overlayPng = CreatePng(4, 1, overlayPixels);
        var material = new XbxMaterial
        {
            NumPasses = 2,
            Passes =
            [
                new XbxPass { TextureChecksum = BaseTextureChecksum },
                new XbxPass { TextureChecksum = OverlayTextureChecksum, BlendMode = 5 }
            ]
        };

        var (png, compositedCount) = XbxPassCompositor.CompositeOverlays(
            material,
            basePng,
            checksum => checksum == OverlayTextureChecksum ? overlayPng : null);

        Assert.Equal(1, compositedCount);
        using var image = Image.Load<Rgba32>(png);
        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            Assert.Equal(overlayPixels[x], image[x, y]);
    }

    private static byte[] CreatePng(int width, int height, IReadOnlyList<Rgba32> pixels)
    {
        Assert.Equal(width * height, pixels.Count);
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = pixels[y * width + x];

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
