using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Ground truth for the PS1 light rigs lifted out of the game binaries.
///
///     The engine shades a dynamically-lit model with a GTE <c>NCCS</c> per
///     normal, with the RGB register pinned to white — so the colour is
///     <c>ambient + ColorMatrix x max(0, LightMatrix . N)</c> and the authored
///     vertex colour is not an input. Expectations below are hand-computed from
///     the fixed-point table values the decomp names, not read back from
///     <see cref="PsxEngineLight.Evaluate" />.
///
///     Which rig applies is runtime context the file never records, so nothing
///     here is applied automatically; these pin the maths and the preset table.
/// </summary>
public sealed class PsxEngineLightTests
{
    // PS1 space is Y-DOWN: -Y faces up on screen, +Y faces down.
    private static readonly Vector3 FacingUp = new(0f, -1f, 0f);
    private static readonly Vector3 FacingDown = new(0f, 1f, 0f);
    private static readonly Vector3 FacingForward = new(0f, 0f, 1f);

    private static readonly string[] ExpectedPresetNames =
        ["item-default", "skater", "skater-mars", "spiderman-player"];

    private static readonly Vector3[] SampleNormals =
    [
        FacingUp, FacingDown, FacingForward,
        Vector3.Normalize(new Vector3(1f, 1f, 1f)),
        Vector3.Normalize(new Vector3(-1f, -1f, -1f))
    ];

    [Fact]
    public void DefaultRig_LightsUpwardFacesWithItsCoolRow()
    {
        // Light 2 is (0,-1,0), so an up-facing normal takes column 2 of each
        // colour row plus ambient: R 0.5625, G 0.8125, B 1.0, ambient 0.125.
        var lit = PsxEngineLight.Default.Evaluate(FacingUp);

        Assert.Equal(0.5625f + 0.125f, lit.X, 3);
        Assert.Equal(0.8125f + 0.125f, lit.Y, 3);
        Assert.Equal(1f, lit.Z, 3); // 1.0 + 0.125 clamps at the 255 ceiling
        Assert.True(lit.Z > lit.X, "up-facing surfaces should read cool");
    }

    [Fact]
    public void DefaultRig_LightsDownwardFacesWithItsWarmRow()
    {
        // Light 1 is (0,+1,0): R 1.0, G 0.5, B 0.125, plus ambient.
        var lit = PsxEngineLight.Default.Evaluate(FacingDown);

        Assert.Equal(1f, lit.X, 3); // 1.0 + 0.125 clamps
        Assert.Equal(0.5f + 0.125f, lit.Y, 3);
        Assert.Equal(0.125f + 0.125f, lit.Z, 3);
        Assert.True(lit.X > lit.Z, "down-facing surfaces should read warm");
    }

    [Fact]
    public void SkaterRig_IsMonochrome()
    {
        // Skater_DefaultLight repeats one row for R, G and B, so every normal
        // yields a neutral grey — this is the table long mislabelled here as a
        // "front-end preview light".
        foreach (var normal in new[] { FacingUp, FacingDown, FacingForward })
        {
            var lit = PsxEngineLight.Skater.Evaluate(normal);
            Assert.Equal(lit.X, lit.Y, 4);
            Assert.Equal(lit.Y, lit.Z, 4);
        }
    }

    [Fact]
    public void SkaterRig_IsBrightestFromAbove()
    {
        // 2944/4096 on light 2 (up-facing) against 640/4096 on light 1, over a
        // 2176/4096 ambient. Up-facing sums to 1.25 and therefore SATURATES:
        // the GTE clamps its output byte at 255, so the rig deliberately blows
        // out surfaces pointing at the key light.
        var up = PsxEngineLight.Skater.Evaluate(FacingUp);
        var down = PsxEngineLight.Skater.Evaluate(FacingDown);

        Assert.Equal(1f, up.X, 3);
        Assert.Equal(2176f / 4096f + 640f / 4096f, down.X, 3);
        Assert.True(up.X > down.X);
    }

    [Fact]
    public void NegativeIntensitiesClampInsteadOfDarkening()
    {
        // GTE lm=1 clamps each light's contribution at zero, so a normal facing
        // away from every light lands on pure ambient rather than going negative.
        var lit = PsxEngineLight.Skater.Evaluate(new Vector3(0f, 0f, -1f));

        Assert.Equal(2176f / 4096f, lit.X, 3);
        Assert.True(lit.X > 0f);
    }

    [Fact]
    public void EveryChannelStaysWithinTheHardwareRange()
    {
        foreach (var (_, light) in PsxEngineLight.Presets)
        {
            foreach (var normal in SampleNormals)
            {
                var lit = light.Evaluate(normal);
                Assert.InRange(lit.X, 0f, 1f);
                Assert.InRange(lit.Y, 0f, 1f);
                Assert.InRange(lit.Z, 0f, 1f);
                Assert.Equal(1f, lit.W);
            }
        }
    }

    [Fact]
    public void PresetLookupIsCaseInsensitiveAndRejectsUnknownNames()
    {
        Assert.Same(PsxEngineLight.Default, PsxEngineLight.FromName("item-default"));
        Assert.Same(PsxEngineLight.Default, PsxEngineLight.FromName("ITEM-DEFAULT"));
        Assert.Same(PsxEngineLight.Skater, PsxEngineLight.FromName(" skater "));
        Assert.Same(PsxEngineLight.SkaterMars, PsxEngineLight.FromName("skater-mars"));

        // Unknown and empty both mean "no bake" rather than a silent fallback to
        // some rig the asset may not use.
        Assert.Null(PsxEngineLight.FromName("nonsense"));
        Assert.Null(PsxEngineLight.FromName(null));
        Assert.Null(PsxEngineLight.FromName("   "));
    }

    [Fact]
    public void PresetsOnlyContainRigsTheDecompNames()
    {
        // Guard against someone adding an address-only rig: every preset must
        // have a known SELECTOR — named by the decomp, or traced to the code
        // that assigns it. Spider-Man's 0x80098E00/E40 are deliberately absent:
        // nothing in the binary references them.
        Assert.Equal(
            ExpectedPresetNames,
            PsxEngineLight.Presets.Keys.OrderBy(static key => key, StringComparer.Ordinal));
    }
}
