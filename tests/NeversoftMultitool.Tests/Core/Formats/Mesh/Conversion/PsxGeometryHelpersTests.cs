using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

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
        palette[1] = new Vector4(1f, 0.7f, 0.3f, 1f);
        palette[2] = new Vector4(0.2f, 0.8f, 0.4f, 1f);
        palette[3] = new Vector4(0.1f, 0.3f, 0.9f, 1f);
        palette[4] = new Vector4(0.9f, 0.4f, 0.2f, 1f);

        var colors = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, palette);

        Assert.Equal(palette[1], colors.C0);
        Assert.Equal(palette[2], colors.C1);
        Assert.Equal(palette[3], colors.C2);
        Assert.Equal(palette[4], colors.C3);
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
}
