using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxGeometryHelpersTests
{
    [Fact]
    public void ComputePsxFaceColors_V6GouraudWithoutPalette_UsesDirectVertexIntensities()
    {
        var face = new PsxFace
        {
            IsGouraud = true,
            IsQuad = true,
            R = 255,
            G = 128,
            B = 64,
            Mode = 0
        };

        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, null);

        Assert.Equal(1f, c0.X);
        Assert.Equal(128f / 255f, c1.X);
        Assert.Equal(64f / 255f, c2.X);
        Assert.Equal(0f, c3.X);
        Assert.Equal(c1.X, c1.Y);
        Assert.Equal(c1.X, c1.Z);
    }

    [Fact]
    public void ComputePsxFaceColors_V6GouraudWithPalette_PreservesColoredLighting()
    {
        var face = new PsxFace
        {
            IsGouraud = true,
            IsQuad = true,
            R = 1,
            G = 2,
            B = 3,
            Mode = 4
        };
        var palette = new Vector4[5];
        palette[1] = new Vector4(128f / 255f, 64f / 255f, 32f / 255f, 1f);
        palette[2] = new Vector4(16f / 255f, 96f / 255f, 48f / 255f, 1f);
        palette[3] = new Vector4(8f / 255f, 24f / 255f, 112f / 255f, 1f);
        palette[4] = new Vector4(104f / 255f, 40f / 255f, 20f / 255f, 1f);

        var colors = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, palette);

        Assert.Equal(palette[1], colors.C0);
        Assert.Equal(palette[2], colors.C1);
        Assert.Equal(palette[3], colors.C2);
        Assert.Equal(palette[4], colors.C3);
        Assert.Equal(128f / 255f, colors.C0.X);
        Assert.NotEqual(1f, colors.C0.X);
    }

    [Fact]
    public void ComputePsxFaceColors_Ps1PaletteUsesFaceAwareGpuScaling()
    {
        var palette = new Vector4[3];
        palette[1] = new Vector4(128f / 255f, 64f / 255f, 251f / 255f, 1f);
        palette[2] = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f);
        var textured = new PsxFace
        {
            IsGouraud = true,
            IsTextured = true,
            IsQuad = false,
            R = 1,
            G = 2,
            B = 1
        };
        var untextured = new PsxFace
        {
            IsGouraud = true,
            IsTextured = false,
            IsQuad = false,
            R = 1,
            G = 2,
            B = 1
        };

        var textureColors = PsxGeometryHelpers.ComputePsxFaceColors(0x04, textured, palette);
        var displayColors = PsxGeometryHelpers.ComputePsxFaceColors(0x04, untextured, palette);

        Assert.Equal(new Vector4(1f, 0.5f, 1f, 1f), textureColors.C0);
        Assert.Equal(new Vector4(1f, 119f / 128f, 1f, 1f), textureColors.C1);
        Assert.Equal(palette[1], displayColors.C0);
        Assert.Equal(palette[2], displayColors.C1);
    }

    [Fact]
    public void ComputePsxFaceColors_UsesModulationOnlyForPs1TexturedFaces()
    {
        var textured = new PsxFace { IsTextured = true, R = 128, G = 64, B = 255 };
        var untextured = new PsxFace { IsTextured = false, R = 128, G = 64, B = 255 };
        var pc = new PsxFace { IsTextured = true, R = 128, G = 64, B = 255 };

        var ps1TextureColor = PsxGeometryHelpers.ComputePsxFaceColors(0x04, textured, null).C0;
        var ps1FlatColor = PsxGeometryHelpers.ComputePsxFaceColors(0x04, untextured, null).C0;
        var pcColor = PsxGeometryHelpers.ComputePsxFaceColors(0x06, pc, null).C0;

        Assert.Equal(1f, ps1TextureColor.X);
        Assert.Equal(0.5f, ps1TextureColor.Y);
        Assert.Equal(128f / 255f, ps1FlatColor.X);
        Assert.Equal(64f / 255f, ps1FlatColor.Y);
        Assert.Equal(128f / 255f, pcColor.X);
        Assert.Equal(64f / 255f, pcColor.Y);
    }

    [Fact]
    public void UntexturedSemiTransparentFace_RetainsAbrMaterialState()
    {
        var face = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var key = PsxGeometryHelpers.GetPsxMaterialKey(face);

        Assert.Equal(0u, key.Hash);
        Assert.True(key.SemiTransparent);
        Assert.Equal(1, key.BlendRate);
    }

    [Fact]
    public void UntexturedAdditiveFace_ConvertsVertexIntensityToAlpha()
    {
        var face = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var converted = PsxGeometryHelpers.ApplyPsxUntexturedBlend(
            face,
            new Vector4(0.25f, 0.5f, 0.125f, 1f));

        Assert.Equal(Vector3.One, new Vector3(converted.X, converted.Y, converted.Z));
        Assert.Equal(0.5f, converted.W);
    }

    [Fact]
    public void DisplayRgbToLinear_AppliesSrgbEotfAndPreservesAlpha()
    {
        var converted = PsxGeometryHelpers.DisplayRgbToLinear(
            new Vector4(0.04045f, 0.5f, 1f, 0.37f));

        Assert.InRange(converted.X, 0.0031307f, 0.0031309f);
        Assert.InRange(converted.Y, 0.2140409f, 0.2140414f);
        Assert.Equal(1f, converted.Z);
        Assert.Equal(0.37f, converted.W);
    }

    [Fact]
    public void DisplayRgbToLinear_RunsAfterNativeBlendProxyAlpha()
    {
        var additive = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var nativeBlend = PsxGeometryHelpers.ApplyPsxUntexturedBlend(
            additive,
            new Vector4(0.25f, 0.5f, 0.125f, 1f));
        var converted = PsxGeometryHelpers.DisplayRgbToLinear(nativeBlend);

        Assert.Equal(Vector3.One, new Vector3(converted.X, converted.Y, converted.Z));
        Assert.Equal(0.5f, converted.W);
    }

    [Fact]
    public void ComputePsxTextureUv_Ps1AddressesTexelCentresWithoutDisablingRepeat()
    {
        var face = new PsxFace { IsTextured = true };

        var first = PsxGeometryHelpers.ComputePsxTextureUv(0x04, face, 0, 0, 64, 32);
        var nextTile = PsxGeometryHelpers.ComputePsxTextureUv(0x04, face, 64, 32, 64, 32);

        Assert.Equal(0.5f / 64f, first.X);
        Assert.Equal(0.5f / 32f, first.Y);
        Assert.Equal(1f + 0.5f / 64f, nextTile.X);
        Assert.Equal(1f + 0.5f / 32f, nextTile.Y);
    }

    [Fact]
    public void ComputePsxTextureUv_V6RetainsFixedPortCoordinateSpace()
    {
        var face = new PsxFace { IsTextured = true };

        var uv = PsxGeometryHelpers.ComputePsxTextureUv(0x06, face, 128, 256, 64, 32);

        Assert.Equal(0.25f, uv.X);
        Assert.Equal(0.5f, uv.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TexturedAbrMaterials_KeepDistinctProcessedTexturesRegardlessOfCreationOrder(
        bool additiveFirst)
    {
        const uint hash = 0x06A567C0u;
        var sourcePng = CreatePng(new Rgba32(64, 128, 192, 255));
        var document = new ModelDocument { Name = "psx-abr-variant" };
        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache =
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();

        var states = new[]
        {
            (SemiTransparent: false, DoubleSided: false, BlendRate: 0),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 0),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 1),
            (SemiTransparent: true, DoubleSided: true, BlendRate: 1),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 2),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 3)
        };
        if (additiveFirst)
            Array.Reverse(states);
        foreach (var state in states)
        {
            PsxGeometryHelpers.GetOrCreatePsxMaterial(
                document,
                hash,
                state.SemiTransparent,
                state.DoubleSided,
                state.BlendRate,
                _ => sourcePng,
                textureDims,
                materialCache);
        }

        // Opaque plus four genuinely different ABR transforms. The one- and
        // two-sided ABR1 materials have byte-identical images and must share.
        Assert.Equal(5, document.Textures.Count);
        Assert.All(document.Textures, texture => Assert.Equal(hash, texture.NativeChecksum));

        var opaqueMaterial = Assert.Single(document.Materials, material => material.Name == "tex_06A567C0");
        var additiveMaterial = Assert.Single(document.Materials, material => material.Name == "tex_06A567C0__st1");
        var additiveTwoSided = Assert.Single(
            document.Materials,
            material => material.Name == "tex_06A567C0__st1__2sided");
        Assert.NotEqual(opaqueMaterial.TextureIndex, additiveMaterial.TextureIndex);
        Assert.Equal(additiveMaterial.TextureIndex, additiveTwoSided.TextureIndex);
        Assert.Equal(ModelAlphaMode.Opaque, opaqueMaterial.AlphaMode);
        Assert.Equal(ModelAlphaMode.Blend, additiveMaterial.AlphaMode);

        using var opaque = Image.Load<Rgba32>(document.Textures[opaqueMaterial.TextureIndex!.Value].PngBytes!);
        using var additive = Image.Load<Rgba32>(document.Textures[additiveMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(64, 128, 192, 255), opaque[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255, 115), additive[0, 0]);
    }

    private static byte[] CreatePng(Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
