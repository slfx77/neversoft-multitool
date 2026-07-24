using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Pins the colour-pulse static-export rule (see
///     <see cref="PsxSurfaceAnimationReader" />): level-object banks keep
///     their raw serialized RGBs palette (Spider-Man renders bank objects at
///     their authored resting colours — the l1a1 "?" bonus marker is dark
///     blue in-game while its pulse keys cycle pure R→G→B), whereas
///     standalone item/prop files bake the pulse's evaluated initial state
///     because their pulsed entries serialize black (the pulse is the only
///     colour source, e.g. items.psx pickup glows).
/// </summary>
public sealed class PsxColourPulseBakeTests(TestPaths paths)
{
    private const string ProtoBuild = "Spider-Man (2000-2-18, PSX - Prototype)";

    [Theory]
    [InlineData("l1a1_o.psx", true)]
    [InlineData("L2A2_O.PSX", true)]
    [InlineData(@"CD\l8a4_o.psx", true)]
    [InlineData("claw.psx", false)]
    [InlineData("items.psx", false)]
    [InlineData("spidey_o.trg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLevelObjectBankName_FollowsNamingConvention(string? fileName, bool expected)
    {
        Assert.Equal(expected, PsxMeshSemantics.IsLevelObjectBankName(fileName));
    }

    [Fact]
    public void LevelObjectBank_KeepsRawPaletteWhenBakeDisabled()
    {
        var bytes = ReadSample("l1a1_o.psx");

        var baked = PsxMeshFile.Parse(bytes);
        var raw = PsxMeshFile.Parse(bytes, bakeColourPulses: false);

        Assert.NotNull(baked?.GouraudPalette);
        Assert.NotNull(raw?.GouraudPalette);

        // Palette entries 38-43 drive the "?" bonus marker: authored dark
        // blues with a staggered pure-R→G→B attract pulse on top. Raw parse
        // keeps the blues; baking evaluates the pulse (entry 38 lands on the
        // red key) — which is what painted the marker rainbow before the
        // _o-bank gate.
        AssertRgb(raw!.GouraudPalette![38], 15, 33, 42);
        AssertRgb(raw.GouraudPalette[42], 19, 34, 42);
        AssertRgb(baked!.GouraudPalette![38], 255, 0, 0);

        // Pulse metadata stays available either way.
        Assert.Equal(6, raw.ColourPulses.Count);
        Assert.Equal(6, baked.ColourPulses.Count);
    }

    [Fact]
    public void ItemFile_BakesPulseOverBlackSerializedPalette()
    {
        var bytes = ReadSample("items.psx");

        var baked = PsxMeshFile.Parse(bytes);
        var raw = PsxMeshFile.Parse(bytes, bakeColourPulses: false);

        Assert.NotNull(baked?.GouraudPalette);
        Assert.NotNull(raw?.GouraudPalette);

        // Entry 19 is the webbing-pickup glow: serialized black, first pulse
        // key (50,100,255). Without the bake the pickup would export black.
        AssertRgb(raw!.GouraudPalette![19], 0, 0, 0);
        AssertRgb(baked!.GouraudPalette![19], 50, 100, 255);
    }

    private byte[] ReadSample(string fileName)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(paths.SampleBuildsDir!, ProtoBuild, "CD", fileName);
        Assert.SkipWhen(!File.Exists(path), $"{fileName} not present in sample builds");
        return File.ReadAllBytes(path);
    }

    private static void AssertRgb(Vector4 actual, byte r, byte g, byte b)
    {
        Assert.Equal(r / 255f, actual.X, 3);
        Assert.Equal(g / 255f, actual.Y, 3);
        Assert.Equal(b / 255f, actual.Z, 3);
    }
}
