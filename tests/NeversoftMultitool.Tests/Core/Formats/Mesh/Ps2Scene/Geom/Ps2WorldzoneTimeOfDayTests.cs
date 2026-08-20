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

    [Theory]
    [InlineData("TOD_NightOn_08", Ps2GeomRenderLayer.NightOverlay)]
    [InlineData("TOD_NightOff_01", Ps2GeomRenderLayer.DayOverlay)]
    // Morning/Afternoon/Evening phases are not representable in the Day/Night
    // binary — such nodes stay in every export.
    [InlineData("TOD_MorningOn_01", Ps2GeomRenderLayer.Base)]
    [InlineData("TOD_AfternoonOff_02", Ps2GeomRenderLayer.Base)]
    public void ClassifyWorldzoneRenderLayer_AuthoredTodGateOverridesTheNameRule(
        string todGroup, Ps2GeomRenderLayer expected)
    {
        // The leaf name says "night salon" (a storefront → Base by name), so a
        // non-Base result proves the createdfromtod gate took precedence.
        var leaf = MakeLeaf(NightSalonChecksum);
        var gates = new Ps2WorldzoneNodeGates(
            false,
            0,
            NeversoftMultitool.Core.QbKey.QbKey.HashLower(todGroup),
            false);
        Assert.Equal(expected, Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf, gates));
    }

    [Fact]
    public void ClassifyWorldzoneRenderLayer_WithoutATodGate_FallsBackToTheNameRule()
    {
        var leaf = MakeLeaf(NightOnChecksum);
        var ungated = new Ps2WorldzoneNodeGates(true, 0, 0, false);
        Assert.Equal(Ps2GeomRenderLayer.NightOverlay,
            Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf, ungated));
        Assert.Equal(Ps2GeomRenderLayer.NightOverlay,
            Ps2GeomRenderSemantics.ClassifyWorldzoneRenderLayer(leaf, null));
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
    ///     z_sm's authored gate census (2026-08-19, createdfromtod authoritative
    ///     over both levelgeometry AND levelobject nodes): 264 night-gated leaf
    ///     instances leave the Day export (the two NightOn-NAMED spot glows
    ///     whose nodes author TOD_NightOff gates correctly STAY) and 89 leave
    ///     the Night export; All keeps every TOD layer. Story-variant leaves
    ///     (createdfromvariable — FERRISAFRAME_BEFORE/AFTER et al.) sit behind
    ///     default-off visibility groups in EVERY time-of-day variant, matching
    ///     the engine's load state (cfuncs.cpp:6276-6281).
    /// </summary>
    [CorpusFact]
    public void ZSm_DayAndNightExports_DropExactlyTheGatedLeafSets()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_sm.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_sm.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var (all, allGroups) = Convert(pakBytes, WorldzoneTimeOfDay.All);
        var (day, _) = Convert(pakBytes, WorldzoneTimeOfDay.Day);
        var (night, _) = Convert(pakBytes, WorldzoneTimeOfDay.Night);

        Assert.Equal(264, all - day);
        // 89 leaf instances; one NightOff leaf places twice → 90 meshes.
        Assert.Equal(90, all - night);
        Assert.Contains(allGroups, static group =>
            group.Id == "thaw.variant.nodeflag_z_sm_ferrisaframe_before" && !group.IsEnabled);
    }

    /// <summary>
    ///     The E7 report ("multiple object setups loaded simultaneously"):
    ///     z_bh's City Hall ships PRE and POST wrecking-ball states that both
    ///     rendered at once. Default export = the engine's load state (neither
    ///     story state active); enabling one default-off visibility group
    ///     restores exactly that state.
    /// </summary>
    [CorpusFact]
    public void ZBh_StoryVariantStates_AreExclusiveAndRestorable()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var preBallBase = NeversoftMultitool.Core.QbKey.QbKey.HashLower("Z_BH_g_cityhall_pre_ball_base");
        var postBallBase = NeversoftMultitool.Core.QbKey.QbKey.HashLower("Z_BH_g_cityhall_post_ball_base");

        var pakBytes = File.ReadAllBytes(pakPath!);
        var (_, defaultGroups, defaultLeaves) = ConvertDocument(pakBytes, "z_bh.pak.ps2",
            WorldzoneTimeOfDay.Day, null);
        Assert.DoesNotContain(defaultLeaves, checksum => checksum == preBallBase || checksum == postBallBase);
        Assert.Contains(defaultGroups, static group =>
            group.Id == "thaw.variant.nodeflag_z_bh_cityhallpost" && !group.IsEnabled);

        var (_, postGroups, postLeaves) = ConvertDocument(pakBytes, "z_bh.pak.ps2",
            WorldzoneTimeOfDay.Day,
            new Dictionary<string, bool> { ["thaw.variant.nodeflag_z_bh_cityhallpost"] = true });
        Assert.Contains(postLeaves, checksum => checksum == postBallBase);
        Assert.DoesNotContain(postLeaves, checksum => checksum == preBallBase);
        Assert.Contains(postGroups, static group =>
            group.Id == "thaw.variant.nodeflag_z_bh_cityhallpost" && group.IsEnabled);
    }

    private static (int MeshCount, IReadOnlyList<ModelVisibilityGroup> Groups) Convert(
        byte[] pakBytes, WorldzoneTimeOfDay timeOfDay)
    {
        var (meshCount, groups, _) = ConvertDocument(pakBytes, "z_sm.pak.ps2", timeOfDay, null);
        return (meshCount, groups);
    }

    private static (int MeshCount, IReadOnlyList<ModelVisibilityGroup> Groups, IReadOnlyList<uint> EmittedLeafChecksums)
        ConvertDocument(
            byte[] pakBytes,
            string sourceName,
            WorldzoneTimeOfDay timeOfDay,
            Dictionary<string, bool>? visibilityOverrides)
    {
        var document = new ModelDocument { Name = $"{sourceName}_{timeOfDay}" };
        var collector = new Ps2GeomDebugCollector(sourceName);
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document, pakBytes, sourceName,
            null, null, null, null, null,
            timeOfDay, 1f,
            debugCollector: collector,
            visibilityOverrides: visibilityOverrides);
        var emitted = collector.Materials
            .Select(static record => record.LeafChecksum)
            .Where(static checksum => checksum != 0)
            .ToList();
        return (document.Meshes.Count, document.VisibilityGroups, emitted);
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
