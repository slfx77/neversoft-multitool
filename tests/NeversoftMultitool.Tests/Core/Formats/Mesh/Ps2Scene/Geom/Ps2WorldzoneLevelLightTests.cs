using System.Text.Json;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     E12 v1 (2026-08-19): the zone's authored Class = levellight nodes —
///     position, colour, radii, exclusions, and TOD/story gates — parse from
///     the node array and ship as GLB scene extras. Data-only: authored
///     brightness is a runtime placeholder (116/117 z_bh lights author 0; the
///     TOD scripts drive live values), so the export never claims engine
///     lighting — that application layer is the A1 follow-up, whose full data
///     model (tod_manager_default_{morning,afternoon,evening,night} world
///     rigs) is documented in the backlog.
/// </summary>
public sealed class Ps2WorldzoneLevelLightTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [CorpusFact]
    public void ZBh_LevelLights_ParseWithAuthoredFieldsAndReachSceneExtras()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var pakBytes = File.ReadAllBytes(pakPath!);
        // 157 = every authored levellight node (inline + template-inherited);
        // the QbKey name inventory counts 158 with one duplicate name deduped.
        var lights = Ps2WorldzoneQbObjectResolver.ResolveLevelLights(
            pakBytes, PakArchive.GetTypedEntries(pakBytes));
        Assert.Equal(157, lights.Count);

        // Z_BH_TRG_Night_LevelLight_04_Street70 — values verbatim from the
        // decompiled node array (TestOutput evidence, 2026-08-19).
        var street70 = Assert.Single(lights, static light =>
            light.NameChecksum == NeversoftMultitool.Core.QbKey.QbKey
                .HashLower("Z_BH_TRG_Night_LevelLight_04_Street70"));
        Assert.Equal(-14472.409f, street70.Position.X, 2);
        Assert.Equal(26.974388f, street70.Position.Y, 2);
        Assert.Equal(9108.219f, street70.Position.Z, 2);
        Assert.Equal((255, 237, 188), (street70.ColorR, street70.ColorG, street70.ColorB));
        Assert.Equal(416.7f, street70.InnerRadius, 1);
        Assert.Equal(538.7f, street70.OuterRadius, 1);
        Assert.Equal(0f, street70.Brightness);
        Assert.Equal(
            NeversoftMultitool.Core.QbKey.QbKey.HashLower("TOD_NightOn_08"),
            street70.CreatedFromTod);

        // End-to-end: the lights ride the document into GLB scene extras.
        var document = new ModelDocument { Name = "z_bh_lights" };
        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document, pakBytes, "z_bh.pak.ps2",
            null, null, null, null, null,
            WorldzoneTimeOfDay.All, 1f);
        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);

        using var json = ParseGlbJson(glbBytes!);
        var scene = json.RootElement.GetProperty("scenes")[0];
        var exported = scene.GetProperty("extras").GetProperty("neversoftLevelLights");
        Assert.Equal(157, exported.GetArrayLength());
        var names = exported.EnumerateArray()
            .Select(static light => light.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("Z_BH_TRG_Night_LevelLight_04_Street70", names);
    }

    private static JsonDocument ParseGlbJson(byte[] glbBytes)
    {
        var jsonLength = BitConverter.ToInt32(glbBytes, 12);
        return JsonDocument.Parse(glbBytes.AsSpan(20, jsonLength).ToArray());
    }
}
