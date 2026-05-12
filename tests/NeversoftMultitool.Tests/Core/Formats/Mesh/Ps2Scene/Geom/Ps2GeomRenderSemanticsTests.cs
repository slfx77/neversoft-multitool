using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomRenderSemanticsTests
{
    [Fact]
    public void OrderWorldzoneLeavesForDraw_UsesRenderGroupThenOriginalLeafIndex()
    {
        var overlay = MakeLeaf(groupChecksum: 12, alpha: 0x44);
        var wear = MakeLeaf(groupChecksum: 6, alpha: 0x44);
        var baseLayer = MakeLeaf(groupChecksum: 5, alpha: 0x0A);

        var ordered = Ps2GeomRenderSemantics.OrderWorldzoneLeavesForDraw([overlay, wear, baseLayer]);

        Assert.Equal([2, 1, 0], ordered.Select(static item => item.LeafIndex).ToArray());
        Assert.Equal([0, 1, 2], ordered.Select(static item => item.DrawIndex).ToArray());
    }

    [Fact]
    public void ClassifyWorldzoneAlphaMode_IgnoresArefWhenAlphaTestIsDisabled()
    {
        var leaf = MakeLeaf(alpha: 0x0A, test: 0x10);

        Assert.Equal("OPAQUE", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf));
    }

    [Fact]
    public void ClassifyWorldzoneAlphaMode_PrefersBlendEquationOverAlphaTestCutout()
    {
        var leaf = MakeLeaf(alpha: 0x44, test: 0x5001B);

        Assert.Equal("BLEND", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf));
    }

    [Fact]
    public void ClassifyWorldzoneAlphaMode_MapsFixedStandardBlendByFixThreshold()
    {
        var lowFix = MakeLeaf(alpha: 0x64UL | (32UL << 32));
        var highFix = MakeLeaf(alpha: 0x64UL | (128UL << 32));

        Assert.Equal("BLEND", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(lowFix));
        Assert.Equal("OPAQUE", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(highFix));
    }

    [Fact]
    public void ClassifyWorldzoneAlphaMode_DoesNotMaskFramebufferOnlyAlphaTestFailure()
    {
        var framebufferOnlyFailure = MakeLeaf(alpha: 0x0A, test: MakeTest(atst: 6, aref: 32, afail: 1));
        var keepFailure = MakeLeaf(alpha: 0x0A, test: MakeTest(atst: 6, aref: 32, afail: 0));

        Assert.Equal("OPAQUE", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(framebufferOnlyFailure));
        Assert.Equal("MASK", Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(keepFailure));
    }

    [Fact]
    public void ComputeAlphaMaskCutoff_UsesExclusiveThresholdForGreaterThanAlphaTest()
    {
        Assert.Equal(32 / 255f, Ps2GeomRenderSemantics.ComputeAlphaMaskCutoff(MakeTest(atst: 5, aref: 32)));
        Assert.Equal(33 / 255f, Ps2GeomRenderSemantics.ComputeAlphaMaskCutoff(MakeTest(atst: 6, aref: 32)));
    }

    [Fact]
    public void BlendUsesSourceAlpha_DetectsBlendFactorSource()
    {
        Assert.True(Ps2GeomRenderSemantics.BlendUsesSourceAlpha(0x44));
        Assert.False(Ps2GeomRenderSemantics.BlendUsesSourceAlpha(0x54));
        Assert.False(Ps2GeomRenderSemantics.BlendUsesSourceAlpha(0x64));
        Assert.True(Ps2GeomRenderSemantics.UsesFixedSourceAlphaBlend(0x64));
        Assert.False(Ps2GeomRenderSemantics.UsesFixedSourceAlphaBlend(0x44));
    }

    [Fact]
    public void TryGetDestinationAlphaSourceMaskMode_DetectsForwardAndInverseMasks()
    {
        Assert.True(Ps2GeomRenderSemantics.TryGetDestinationAlphaSourceMaskMode(0x54, out var forwardInvert));
        Assert.False(forwardInvert);

        Assert.True(Ps2GeomRenderSemantics.TryGetDestinationAlphaSourceMaskMode(0x11, out var inverseInvert));
        Assert.True(inverseInvert);

        Assert.False(Ps2GeomRenderSemantics.TryGetDestinationAlphaSourceMaskMode(0x44, out _));
    }

    [Fact]
    public void WritesFramebufferAlpha_DetectsFrameAlphaWriteMask()
    {
        Assert.True(Ps2GeomRenderSemantics.WritesFramebufferAlpha(MakeLeaf(frame: 0)));
        Assert.True(Ps2GeomRenderSemantics.WritesFramebufferAlpha(MakeLeaf(frame: MakeFrame(fbmsk: 0x00FFFFFF))));
        Assert.False(Ps2GeomRenderSemantics.WritesFramebufferAlpha(MakeLeaf(frame: MakeFrame(fbmsk: 0xFF000000))));
    }

    private static Ps2GeomLeaf MakeLeaf(
        uint groupChecksum = 0,
        ulong alpha = 0x0A,
        ulong test = 0,
        ulong frame = 0,
        bool isBillboard = false)
    {
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = groupChecksum,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                new Ps2Vertex(Vector3.Zero, Vector3.UnitZ, 128, 128, 128, 128, 0, 0, true, true, true, false),
                new Ps2Vertex(Vector3.UnitX, Vector3.UnitZ, 128, 128, 128, 128, 1, 0, true, true, true, false),
                new Ps2Vertex(Vector3.UnitY, Vector3.UnitZ, 128, 128, 128, 128, 0, 1, true, true, true, false)
            ],
            DmaAlpha1 = alpha,
            DmaTest1 = test,
            DmaFrame1 = frame,
            IsBillboard = isBillboard
        };
    }

    private static ulong MakeFrame(uint fbmsk) => (ulong)fbmsk << 32;

    private static ulong MakeTest(int atst, int aref, int afail = 0) =>
        0x1UL |
        ((ulong)(atst & 0x7) << 1) |
        ((ulong)(aref & 0xFF) << 4) |
        ((ulong)(afail & 0x3) << 12);
}
