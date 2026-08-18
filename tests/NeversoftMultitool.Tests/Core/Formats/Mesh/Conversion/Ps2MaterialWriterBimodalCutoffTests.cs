using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     B8 regression (z_ho chainlink, 2026-08-18): a bimodal cutout texture
///     de-escalated BLEND→MASK must not take its cutoff from the engine's
///     always-pass alpha test (ATE=1/AGEQUAL/AREF=1 → 1/128), which the α≈2
///     hole texels pass — under the app's filtered sampling the fence rendered
///     solid. The cutout lives in the texture, so the cutoff is 0.5 unless the
///     register carries a DELIBERATE threshold (AREF ≥ 2), which stays authored.
/// </summary>
public sealed class Ps2MaterialWriterBimodalCutoffTests
{
    private const uint TextureChecksum = 0x711D88D6;

    // GS ALPHA_1: A=Cs, B=Cd, C=As, D=Cd — the standard source-alpha blend.
    private const ulong StandardBlendAlpha = 0x44;

    // GS TEST_1: ATE=1, ATST=GEQUAL(5), AREF=1, AFAIL=0 — the engine-default
    // always-pass test (kills only a == 0).
    private const ulong EngineDefaultTest = 0x1BUL;

    // Same test with a deliberately programmed AREF of 50.
    private const ulong DeliberateArefTest = 0x32BUL;

    [Fact]
    public void BimodalMask_EngineDefaultTest_UsesHalfCutoff()
    {
        var material = Apply(StandardBlendAlpha, EngineDefaultTest);

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        Assert.Equal(0.5f, material.AlphaCutoff);
    }

    [Fact]
    public void BimodalMask_DeliberateAref_KeepsAuthoredCutoff()
    {
        var material = Apply(StandardBlendAlpha, DeliberateArefTest);

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        Assert.Equal(50f / 128f, material.AlphaCutoff);
    }

    [Fact]
    public void BimodalMask_WorldzoneAlphaModeOverride_StillUsesHalfCutoff()
    {
        // The worldzone writer pre-computes the mode and passes it as the
        // override — the cutoff rule must still see the bimodal texture.
        var material = Apply(StandardBlendAlpha, EngineDefaultTest, alphaModeOverride: "MASK");

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        Assert.Equal(0.5f, material.AlphaCutoff);
    }

    [Fact]
    public void RegisterOnlyMask_KeepsComputedRegisterCutoff()
    {
        // No blend programmed: MASK comes from the register alone, and the
        // texture-based floor must NOT apply — treating the default test as a
        // half-alpha cutout is exactly the 884d018 regression shape.
        var material = Apply(alpha: 0x00, test: EngineDefaultTest);

        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        Assert.Equal(1f / 128f, material.AlphaCutoff);
    }

    private static RenderMaterial Apply(ulong alpha, ulong test, string? alphaModeOverride = null)
    {
        var document = new ModelDocument { Name = "b8_bimodal_cutoff" };
        var material = new RenderMaterial { Name = "fence" };
        document.Materials.Add(material);

        var png = CreateBimodalPng();
        Ps2MaterialWriter.ApplyPs2GeomMaterial(
            document,
            material,
            new Ps2GeomLeaf
            {
                TextureChecksum = TextureChecksum,
                DmaAlpha1 = alpha,
                DmaTest1 = test,
                Vertices = []
            },
            checksum => checksum == TextureChecksum ? png : null,
            null,
            alphaModeOverride: alphaModeOverride);
        return material;
    }

    /// <summary>Half hole texels (α=2, the z_ho fence's actual hole alpha), half solid.</summary>
    private static byte[] CreateBimodalPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(90, 90, 90, 2);
        image[1, 0] = new Rgba32(90, 90, 90, 255);
        image[0, 1] = new Rgba32(90, 90, 90, 2);
        image[1, 1] = new Rgba32(90, 90, 90, 255);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
