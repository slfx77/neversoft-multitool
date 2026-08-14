using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class Ps2MaterialWriterTextureVariantTests
{
    private const uint TextureChecksum = 0x12345678;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameChecksum_DifferentWrapsRegisterDistinctVariants(bool useGeomPath)
    {
        var (document, firstMaterial, secondMaterial) =
            ApplyMaterials(useGeomPath, firstClamps: false, secondClamps: true);

        Assert.Equal(2, document.Textures.Count);
        var firstIndex = Assert.IsType<int>(firstMaterial.TextureIndex);
        var secondIndex = Assert.IsType<int>(secondMaterial.TextureIndex);
        Assert.NotEqual(firstIndex, secondIndex);

        var repeat = document.Textures[firstIndex];
        var clamp = document.Textures[secondIndex];
        Assert.Equal(TextureChecksum, repeat.NativeChecksum);
        Assert.Equal(TextureChecksum, clamp.NativeChecksum);
        Assert.Equal(ModelTextureWrap.Repeat, repeat.WrapU);
        Assert.Equal(ModelTextureWrap.ClampToEdge, clamp.WrapU);
        Assert.Equal(ModelTextureWrap.Repeat, repeat.WrapV);
        Assert.Equal(ModelTextureWrap.Repeat, clamp.WrapV);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameChecksum_IdenticalWrapsDeduplicate(bool useGeomPath)
    {
        var (document, firstMaterial, secondMaterial) =
            ApplyMaterials(useGeomPath, firstClamps: true, secondClamps: true);

        var texture = Assert.Single(document.Textures);
        Assert.Equal(TextureChecksum, texture.NativeChecksum);
        Assert.Equal(ModelTextureWrap.ClampToEdge, texture.WrapU);
        Assert.Equal(ModelTextureWrap.Repeat, texture.WrapV);
        Assert.Equal(firstMaterial.TextureIndex, secondMaterial.TextureIndex);
    }

    private static (ModelDocument Document, RenderMaterial First, RenderMaterial Second) ApplyMaterials(
        bool useGeomPath,
        bool firstClamps,
        bool secondClamps)
    {
        var document = new ModelDocument { Name = "ps2_texture_variants" };
        var firstMaterial = new RenderMaterial { Name = "first" };
        var secondMaterial = new RenderMaterial { Name = "second" };
        document.Materials.Add(firstMaterial);
        document.Materials.Add(secondMaterial);

        var png = CreatePngBytes();
        byte[]? ResolveTexture(uint checksum) => checksum == TextureChecksum ? png : null;

        if (useGeomPath)
        {
            Ps2MaterialWriter.ApplyPs2GeomMaterial(
                document,
                firstMaterial,
                CreateGeomLeaf(firstClamps),
                ResolveTexture,
                null);
            Ps2MaterialWriter.ApplyPs2GeomMaterial(
                document,
                secondMaterial,
                CreateGeomLeaf(secondClamps),
                ResolveTexture,
                null);
        }
        else
        {
            Ps2MaterialWriter.ApplyPs2Material(
                document,
                firstMaterial,
                CreateSceneMaterial(firstClamps),
                ResolveTexture);
            Ps2MaterialWriter.ApplyPs2Material(
                document,
                secondMaterial,
                CreateSceneMaterial(secondClamps),
                ResolveTexture);
        }

        return (document, firstMaterial, secondMaterial);
    }

    private static Ps2Material CreateSceneMaterial(bool clampU)
    {
        return new Ps2Material
        {
            TextureChecksum = TextureChecksum,
            ClampUMode = clampU ? 1u : 0u,
            ClampVMode = 0
        };
    }

    private static Ps2GeomLeaf CreateGeomLeaf(bool clampU)
    {
        return new Ps2GeomLeaf
        {
            TextureChecksum = TextureChecksum,
            DmaClamp1 = clampU ? 1ul : 0ul,
            Vertices = []
        };
    }

    private static byte[] CreatePngBytes()
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(32, 64, 96, 255);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
