using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class SoftwareRasterizerTests
{
    [Fact]
    public void RasterizeTriangle_TexturedOverbrightColorModulatesInLinearSpace()
    {
        var pixels = new byte[4];
        var depth = new[] { float.NegativeInfinity };
        var modulation = PsxGeometryHelpers.DisplayRgbToLinear(
            new Vector4(144f / 128f, 144f / 128f, 144f / 128f, 1f),
            true);
        var triangle = new RenderTriangle
        {
            Sx0 = 0f,
            Sy0 = 0f,
            Z0 = 1f,
            Sx1 = 0f,
            Sy1 = 2f,
            Z1 = 1f,
            Sx2 = 2f,
            Sy2 = 0f,
            Z2 = 1f,
            R0 = modulation.X,
            G0 = modulation.Y,
            B0 = modulation.Z,
            A0 = 1f,
            R1 = modulation.X,
            G1 = modulation.Y,
            B1 = modulation.Z,
            A1 = 1f,
            R2 = modulation.X,
            G2 = modulation.Y,
            B2 = modulation.Z,
            A2 = 1f,
            HasVertexColors = true,
            FlatShade = 1f
        };
        var submesh = new RenderSubmesh
        {
            Positions = [],
            Triangles = [],
            TextureData = [131, 131, 131, 255],
            TextureWidth = 1,
            TextureHeight = 1
        };

        SoftwareRasterizer.RasterizeTriangle(pixels, depth, 1, 1, triangle, [submesh]);

        Assert.InRange(pixels[0], 143, 144);
        Assert.InRange(pixels[1], 143, 144);
        Assert.InRange(pixels[2], 143, 144);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void RasterizeTriangle_MaskedMaterialWithZeroFactorAlpha_PreservesColorAndDepth()
    {
        AssertZeroAlphaFragmentIsDiscarded(baseColorAlpha: 0f, vertexAlpha: 1f, hasVertexColors: false);
    }

    [Fact]
    public void RasterizeTriangle_MaskedMaterialWithZeroVertexAlpha_PreservesColorAndDepth()
    {
        AssertZeroAlphaFragmentIsDiscarded(baseColorAlpha: 1f, vertexAlpha: 0f, hasVertexColors: true);
    }

    [Theory]
    [InlineData(true, 255, 63, 0)]
    [InlineData(false, 200, 50, 0)]
    public void RasterizeTriangle_UntexturedMaterial_AppliesBaseColorFactor(
        bool hasVertexColors,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue)
    {
        var pixels = new byte[4];
        var depth = new[] { float.NegativeInfinity };
        var triangle = new RenderTriangle
        {
            Sx0 = 0f,
            Sy0 = 0f,
            Z0 = 1f,
            Sx1 = 0f,
            Sy1 = 2f,
            Z1 = 1f,
            Sx2 = 2f,
            Sy2 = 0f,
            Z2 = 1f,
            R0 = 1f,
            G0 = 1f,
            B0 = 1f,
            A0 = 1f,
            R1 = 1f,
            G1 = 1f,
            B1 = 1f,
            A1 = 1f,
            R2 = 1f,
            G2 = 1f,
            B2 = 1f,
            A2 = 1f,
            HasVertexColors = hasVertexColors,
            FlatShade = 1f
        };
        var submesh = new RenderSubmesh
        {
            Positions = [],
            Triangles = [],
            BaseColorR = 1f,
            BaseColorG = 0.25f,
            BaseColorB = 0f
        };

        SoftwareRasterizer.RasterizeTriangle(pixels, depth, 1, 1, triangle, [submesh]);

        Assert.Equal([expectedRed, expectedGreen, expectedBlue, (byte)255], pixels);
    }

    private static void AssertZeroAlphaFragmentIsDiscarded(
        float baseColorAlpha,
        float vertexAlpha,
        bool hasVertexColors)
    {
        byte[] pixels = [11, 22, 33, 44];
        var depth = new[] { float.NegativeInfinity };
        var triangle = new RenderTriangle
        {
            Sx0 = 0f,
            Sy0 = 0f,
            Z0 = 1f,
            Sx1 = 0f,
            Sy1 = 2f,
            Z1 = 1f,
            Sx2 = 2f,
            Sy2 = 0f,
            Z2 = 1f,
            R0 = 1f,
            G0 = 1f,
            B0 = 1f,
            A0 = vertexAlpha,
            R1 = 1f,
            G1 = 1f,
            B1 = 1f,
            A1 = vertexAlpha,
            R2 = 1f,
            G2 = 1f,
            B2 = 1f,
            A2 = vertexAlpha,
            HasVertexColors = hasVertexColors,
            FlatShade = 1f
        };
        var submesh = new RenderSubmesh
        {
            Positions = [],
            Triangles = [],
            TextureData = [255, 255, 255, 255],
            TextureWidth = 1,
            TextureHeight = 1,
            BaseColorA = baseColorAlpha,
            AlphaMode = 1,
            AlphaCutoff = 0.5f
        };

        SoftwareRasterizer.RasterizeTriangle(pixels, depth, 1, 1, triangle, [submesh]);

        Assert.Equal([11, 22, 33, 44], pixels);
        Assert.Equal(float.NegativeInfinity, depth[0]);
    }
}
