using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomDestinationAlphaSynthesisTests
{
    [Fact]
    public void TryCreateSyntheticTexture_PrefersLatestExactCoplanarMask()
    {
        const uint oldMaskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        const uint newerMaskChecksum = 0x55556666;

        var oldMaskLeaf = MakeTexturedLeaf(oldMaskChecksum, alpha1: 0x44);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, alpha1: 0x54);
        var newerMaskLeaf = MakeTexturedLeaf(newerMaskChecksum, alpha1: 0x44);
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw(
            [oldMaskLeaf, layerLeaf, newerMaskLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [oldMaskChecksum] = MakeUniformAlphaPng(64),
            [newerMaskChecksum] = MakeUniformAlphaPng(192),
            [layerChecksum] = MakeSolidPng(new Rgba32(0, 64, 255, 255))
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var syntheticChecksum);

        Assert.True(created);
        Assert.True((syntheticChecksum & 0x80000000u) != 0);
        var pixel = ReadFirstPixel(syntheticTextures[syntheticChecksum]);
        Assert.Equal(new Rgba32(0, 64, 255, 192), pixel);
    }

    [Fact]
    public void TryCreateSyntheticTexture_RespectsMaskUvTransform()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var layerLeaf = MakeTexturedQuadLeaf(layerChecksum, alpha1: 0x54,
        [
            (0f, 0f),
            (1f, 0f),
            (0f, 1f),
            (1f, 1f)
        ]);
        var maskLeaf = MakeTexturedQuadLeaf(maskChecksum, alpha1: 0x44,
        [
            (1f, 0f),
            (0f, 0f),
            (1f, 1f),
            (0f, 1f)
        ]);
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([layerLeaf, maskLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [maskChecksum] = MakeHorizontalAlphaMaskPng(),
            [layerChecksum] = MakeSolidPng(new Rgba32(0, 64, 255, 255), width: 2, height: 2)
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var syntheticChecksum);

        Assert.True(created);
        var pixel = ReadFirstPixel(syntheticTextures[syntheticChecksum]);
        Assert.Equal(new Rgba32(0, 64, 255, 200), pixel);
    }

    [Fact]
    public void TryCreateSyntheticTexture_InvertsMaskForReverseDestinationAlphaBlend()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, alpha1: 0x44);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, alpha1: 0x11);
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([maskLeaf, layerLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [maskChecksum] = MakeUniformAlphaPng(64),
            [layerChecksum] = MakeSolidPng(new Rgba32(0, 64, 255, 255))
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var syntheticChecksum);

        Assert.True(created);
        var pixel = ReadFirstPixel(syntheticTextures[syntheticChecksum]);
        Assert.Equal(new Rgba32(0, 64, 255, 191), pixel);
    }

    [Fact]
    public void TryCreateSyntheticTexture_UsesDestinationAlphaInsteadOfSourceAlphaAsBlendFactor()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, alpha1: 0x44);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, alpha1: 0x54, test1: MakeTest(atst: 5, aref: 0));
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([maskLeaf, layerLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [maskChecksum] = MakeUniformAlphaPng(192),
            [layerChecksum] = MakeSolidPng(new Rgba32(0, 64, 255, 64))
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var syntheticChecksum);

        Assert.True(created);
        var pixel = ReadFirstPixel(syntheticTextures[syntheticChecksum]);
        Assert.Equal(new Rgba32(0, 64, 255, 192), pixel);
    }

    [Fact]
    public void TryCreateSyntheticTexture_UsesSourceAlphaTestAsBinaryCoverage()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, alpha1: 0x44);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, alpha1: 0x54, test1: MakeTest(atst: 5, aref: 1));
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([maskLeaf, layerLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [maskChecksum] = MakeUniformAlphaPng(192),
            [layerChecksum] = MakeTwoPixelAlphaPng(0, 64)
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var syntheticChecksum);

        Assert.True(created);
        using var image = Image.Load<Rgba32>(syntheticTextures[syntheticChecksum]);
        Assert.Equal(new Rgba32(0, 64, 255, 0), image[0, 0]);
        Assert.Equal(new Rgba32(0, 64, 255, 192), image[1, 0]);
    }

    [Fact]
    public void TryCreateSyntheticTexture_IgnoresMaskSourceThatCannotWriteFramebufferAlpha()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, alpha1: 0x44, frame1: MakeFrame(fbmsk: 0xFF000000));
        var layerLeaf = MakeTexturedLeaf(layerChecksum, alpha1: 0x54);
        var orderedLeaves = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([maskLeaf, layerLeaf]);
        var textures = new Dictionary<uint, byte[]>
        {
            [maskChecksum] = MakeUniformAlphaPng(64),
            [layerChecksum] = MakeSolidPng(new Rgba32(0, 64, 255, 255))
        };
        var candidates = Ps2GeomDestinationAlphaSynthesis.BuildMaskCandidates(
            orderedLeaves,
            (checksum, _) => textures.GetValueOrDefault(checksum),
            tex0Resolver: null,
            leafFilter: null,
            skipLeaf: null);
        var syntheticTextures = new Dictionary<uint, byte[]>();

        using var _ = WithDestinationAlphaStrategy("synthesize");
        var created = Ps2GeomDestinationAlphaSynthesis.TryCreateSyntheticTexture(
            layerLeaf,
            layerChecksum,
            Ps2GeomRenderSemantics.GetWorldzoneRenderOrderKey(layerLeaf),
            candidates,
            recentExactMasks: new Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate>(),
            (checksum, _) => textures.GetValueOrDefault(checksum) ?? syntheticTextures.GetValueOrDefault(checksum),
            syntheticTextures,
            out var ignoredSyntheticChecksum);

        Assert.Empty(candidates);
        Assert.False(created);
        Assert.Equal(0u, ignoredSyntheticChecksum);
    }

    [Fact]
    public void ClassifyTextureAlphaMode_MapsOpaqueBimodalAndGraduatedAlpha()
    {
        Assert.Equal("OPAQUE", Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(
            MakeSolidPng(new Rgba32(255, 255, 255, 255))));
        Assert.Equal("MASK", Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(
            MakeBimodalAlphaPng()));
        Assert.Equal("BLEND", Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(
            MakeUniformAlphaPng(128)));
    }

    [Fact]
    public void ShouldFallbackToSourceAlphaBlend_OnlyForBlendStrategyDestinationAlpha()
    {
        var destAlphaLeaf = MakeTexturedLeaf(0x33334444, alpha1: 0x54);
        var sourceAlphaLeaf = MakeTexturedLeaf(0x33334444, alpha1: 0x44);

        using (WithDestinationAlphaStrategy("synthesize"))
        {
            Assert.False(Ps2GeomDestinationAlphaSynthesis.ShouldFallbackToSourceAlphaBlend(destAlphaLeaf));
        }

        using (WithDestinationAlphaStrategy("blend"))
        {
            Assert.True(Ps2GeomDestinationAlphaSynthesis.ShouldFallbackToSourceAlphaBlend(destAlphaLeaf));
            Assert.False(Ps2GeomDestinationAlphaSynthesis.ShouldFallbackToSourceAlphaBlend(sourceAlphaLeaf));
        }
    }

    private static RestoreEnvironmentVariable WithDestinationAlphaStrategy(string strategy)
    {
        var previous = Environment.GetEnvironmentVariable("THAW_DEST_ALPHA");
        Environment.SetEnvironmentVariable("THAW_DEST_ALPHA", strategy);
        return new RestoreEnvironmentVariable("THAW_DEST_ALPHA", previous);
    }

    private static Ps2GeomLeaf MakeTexturedLeaf(uint textureChecksum, ulong alpha1, ulong frame1 = 0, ulong test1 = 0)
    {
        return new Ps2GeomLeaf
        {
            TextureChecksum = textureChecksum,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeUvVertex(0, 0, 0, 0),
                MakeUvVertex(1, 0, 1, 0),
                MakeUvVertex(0, 1, 0, 1),
                MakeUvVertex(1, 1, 1, 1)
            ],
            DmaAlpha1 = alpha1,
            DmaTest1 = test1,
            DmaFrame1 = frame1
        };
    }

    private static Ps2GeomLeaf MakeTexturedQuadLeaf(
        uint textureChecksum,
        ulong alpha1,
        IReadOnlyList<(float U, float V)> uvs)
    {
        Assert.Equal(4, uvs.Count);
        return new Ps2GeomLeaf
        {
            TextureChecksum = textureChecksum,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeUvVertex(0, 0, uvs[0].U, uvs[0].V),
                MakeUvVertex(1, 0, uvs[1].U, uvs[1].V),
                MakeUvVertex(0, 1, uvs[2].U, uvs[2].V),
                MakeUvVertex(1, 1, uvs[3].U, uvs[3].V)
            ],
            DmaAlpha1 = alpha1
        };
    }

    private static ulong MakeFrame(uint fbmsk) => (ulong)fbmsk << 32;

    private static ulong MakeTest(int atst, int aref, int afail = 0) =>
        0x1UL |
        ((ulong)(atst & 0x7) << 1) |
        ((ulong)(aref & 0xFF) << 4) |
        ((ulong)(afail & 0x3) << 12);

    private static Ps2Vertex MakeUvVertex(float x, float y, float u, float v)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitZ,
            128, 128, 128, 128,
            u, v,
            true,
            false,
            true,
            false);
    }

    private static byte[] MakeSolidPng(Rgba32 color, int width = 1, int height = 1)
    {
        using var image = new Image<Rgba32>(width, height, color);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeUniformAlphaPng(byte alpha)
    {
        return MakeSolidPng(new Rgba32(255, 255, 255, alpha), width: 2, height: 2);
    }

    private static byte[] MakeHorizontalAlphaMaskPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(255, 255, 255, 0);
        image[1, 0] = new Rgba32(255, 255, 255, 200);
        image[0, 1] = new Rgba32(255, 255, 255, 0);
        image[1, 1] = new Rgba32(255, 255, 255, 200);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeTwoPixelAlphaPng(byte firstAlpha, byte secondAlpha)
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(0, 64, 255, firstAlpha);
        image[1, 0] = new Rgba32(0, 64, 255, secondAlpha);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeBimodalAlphaPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(255, 255, 255, 0);
        image[1, 0] = new Rgba32(255, 255, 255, 255);
        image[0, 1] = new Rgba32(255, 255, 255, 255);
        image[1, 1] = new Rgba32(255, 255, 255, 0);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Rgba32 ReadFirstPixel(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        return image[0, 0];
    }

    private sealed class RestoreEnvironmentVariable(string name, string? previous) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
