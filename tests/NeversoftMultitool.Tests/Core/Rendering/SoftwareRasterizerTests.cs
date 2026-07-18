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
            isPs1TexturedModulation: true);
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
}
