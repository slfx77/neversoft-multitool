using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Rendering;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class GlbRendererTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void RenderToFile_BareOutputName_WritesToCurrentDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"nmt-glb-renderer-{Guid.NewGuid():N}");
        var outputName = $"nmt-glb-renderer-{Guid.NewGuid():N}.png";
        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), outputName);

        try
        {
            Directory.CreateDirectory(temp);
            var input = Path.Combine(temp, "empty.glb");
            File.WriteAllBytes(input, BuildEmptySceneGlb());

            GlbRenderer.RenderToFile(input, outputName, longEdge: 8);

            Assert.True(File.Exists(outputPath));
            Assert.Equal(PngSignature, File.ReadAllBytes(outputPath)[..PngSignature.Length]);
        }
        finally
        {
            File.Delete(outputPath);
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderScene_FixedCanvasTransparentExit_PreservesRequestedDimensions(
        bool hasDegenerateTriangle)
    {
        var scene = new RenderScene();
        scene.Submeshes.Add(new RenderSubmesh
        {
            Positions = hasDegenerateTriangle
                ? [1f, 2f, 3f, 1f, 2f, 3f, 1f, 2f, 3f]
                : [],
            Triangles = hasDegenerateTriangle ? [0, 1, 2] : []
        });

        using var image = GlbRenderer.RenderScene(
            scene,
            longEdge: 64,
            fixedWidth: 24,
            fixedHeight: 12);

        Assert.Equal(24, image.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[23, 11]);
    }

    private static byte[] BuildEmptySceneGlb()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{}]}");
        var paddedJsonLength = (json.Length + 3) & ~3;
        var data = new byte[12 + 8 + paddedJsonLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x4E4F534A); // JSON
        json.AsSpan().CopyTo(data.AsSpan(20));
        data.AsSpan(20 + json.Length, paddedJsonLength - json.Length).Fill(0x20);
        return data;
    }
}
