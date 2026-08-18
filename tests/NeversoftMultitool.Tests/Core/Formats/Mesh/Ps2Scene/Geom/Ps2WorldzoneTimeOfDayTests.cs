using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Name-driven time-of-day classification (2026-08-18): THAW's TOD system
///     toggles nodes whose authored names carry NightOn_NN / NightOff_NN
///     markers (the QB corpus's TOD_NightOn_NN / TOD_NightOff_NN groups). The
///     retired additive-blend heuristic contradicted those tags in both
///     directions. Checksums below are real worldzone node names resolved via
///     the QbKey tables (harvested from the THAW debug archives).
/// </summary>
public sealed class Ps2WorldzoneTimeOfDayTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    // Z_HO_Nighton_01_stores_Billboard_glow — note the mixed-case authored tag.
    private const uint NightOnChecksum = 0x9B6319CF;

    // Z_HO_NightOff_01_HO_stores_shadows — a DAYTIME light shadow.
    private const uint NightOffChecksum = 0x89792312;

    // Z_HO_HO_stores_night_salon — a storefront NAMED "night salon"; the bare
    // "night" substring must NOT classify it (it is always-on geometry).
    private const uint NightSalonChecksum = 0x95B7C2F6;

    // Z_SM_NightOn_02_Palm_Lights_01 — non-additive night content the old
    // additive heuristic kept in Day exports.
    private const uint PalmLightsChecksum = 0x40151BA6;

    [Theory]
    [InlineData(NightOnChecksum, Ps2GeomRenderLayer.NightOverlay)]
    [InlineData(PalmLightsChecksum, Ps2GeomRenderLayer.NightOverlay)]
    [InlineData(NightOffChecksum, Ps2GeomRenderLayer.DayOverlay)]
    [InlineData(NightSalonChecksum, Ps2GeomRenderLayer.Base)]
    [InlineData(0u, Ps2GeomRenderLayer.Base)]
    public void ClassifyWorldzoneRenderLayer_FollowsAuthoredNameTags(
        uint checksum, Ps2GeomRenderLayer expected)
    {
        var leaf = MakeLeaf(checksum);
        Assert.Equal(expected, Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf));
    }

    [Fact]
    public void AdditiveBlendAloneNoLongerClassifiesNightOverlay()
    {
        // Additive GS blend (A=Cs B=0 C=As D=Cd = 0x48-class) with no name:
        // always-on effect (interior lights, steam, graffiti) — stays Base.
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            DmaAlpha1 = 0x48,
            Vertices = []
        };
        Assert.Equal(Ps2GeomRenderLayer.Base,
            Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf));
    }

    /// <summary>
    ///     z_sm's authored tag census: 277 NightOn leaves leave the Day export
    ///     and 81 NightOff leaves (one placed twice → 82 meshes) leave the
    ///     Night export; All keeps everything.
    /// </summary>
    [CorpusFact]
    public void ZSm_DayAndNightExports_DropExactlyTheTaggedLeafSets()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_sm.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_sm.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var all = Convert(pakBytes, WorldzoneTimeOfDay.All);
        var day = Convert(pakBytes, WorldzoneTimeOfDay.Day);
        var night = Convert(pakBytes, WorldzoneTimeOfDay.Night);

        Assert.Equal(277, all - day);
        Assert.Equal(82, all - night);
    }

    private static int Convert(byte[] pakBytes, WorldzoneTimeOfDay timeOfDay)
    {
        var document = new ModelDocument { Name = $"z_sm_tod_{timeOfDay}" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document, pakBytes, "z_sm.pak.ps2",
            null, null, null, null, null,
            timeOfDay, 1f);
        return document.Meshes.Count;
    }

    private static Ps2GeomLeaf MakeLeaf(uint checksum)
    {
        return new Ps2GeomLeaf
        {
            Checksum = checksum,
            Vertices = []
        };
    }
}
