using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene;

/// <summary>
///     Mission worldzone paks (missions/worldzones/m_z*) stream on top of their
///     base zone in-game and depend on its texture dictionaries — the base zone
///     is selected by longest underscore-stripped zone-name prefix and its paks
///     pool AFTER mission-local sources (first-wins keeps local overrides).
/// </summary>
public sealed class ZoneTextureProviderBuilderMissionTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [Fact]
    public void SelectMissionBaseZone_PicksLongestStrippedPrefix()
    {
        string[] zones = ["z_bh", "z_bhsm", "z_bhho", "z_ho", "z_mainmenu"];

        Assert.Equal("z_bh", ZoneTextureProviderBuilder.SelectMissionBaseZone(
            "m_zbhgaps4_success.pak.ps2", zones));
        Assert.Equal("z_ho", ZoneTextureProviderBuilder.SelectMissionBaseZone(
            "m_zhogaps13_gameplay.pak.ps2", zones));
        // A transition-zone mission takes the longer match, never the z_bh prefix.
        Assert.Equal("z_bhsm", ZoneTextureProviderBuilder.SelectMissionBaseZone(
            "m_zbhsmrace.pak.ps2", zones));
        // Non-mission names never map.
        Assert.Null(ZoneTextureProviderBuilder.SelectMissionBaseZone("z_bh.pak.ps2", zones));
        Assert.Null(ZoneTextureProviderBuilder.SelectMissionBaseZone("qb.pak.ps2", zones));
    }

    [CorpusFact]
    public void EveryMissionFamily_MapsToExactlyOneExistingZone()
    {
        var buildRoot = paths.SampleBuildsDir != null
            ? Path.Combine(paths.SampleBuildsDir, ThawPs2Build, "DATAP")
            : null;
        Assert.SkipWhen(buildRoot == null || !Directory.Exists(buildRoot), "THAW PS2 DATAP tree not available");

        var missionsRoot = Path.Combine(buildRoot!, "missions", "worldzones");
        var zonesRoot = Path.Combine(buildRoot!, "worlds", "worldzones");
        Assert.SkipWhen(!Directory.Exists(missionsRoot) || !Directory.Exists(zonesRoot),
            "missions/worldzones tree not available");

        var zones = Directory.EnumerateDirectories(zonesRoot)
            .Select(static d => Path.GetFileName(d)!)
            .ToList();
        var unmapped = new List<string>();
        foreach (var family in Directory.EnumerateDirectories(missionsRoot))
        {
            var name = Path.GetFileName(family)!;
            if (ZoneTextureProviderBuilder.SelectMissionBaseZone(name + ".pak.ps2", zones) == null)
                unmapped.Add(name);
        }

        Assert.Empty(unmapped);
    }

    [CorpusFact]
    public void MissionPak_PoolsItsBaseZoneDictionariesLast()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "m_zbhgaps4_success.pak.ps2");
        Assert.SkipWhen(pakPath is null, "m_zbhgaps4_success.pak.ps2 not found");

        var sources = ZoneTextureProviderBuilder.GetTexFiles(pakPath!);
        Assert.Equal(pakPath, sources[0]); // mission-local stays first (first-wins priority)
        Assert.Contains(sources, static s =>
            Path.GetFileName(s).Equals("z_bh.pak.ps2", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            sources.FindIndex(static s =>
                Path.GetFileName(s).Equals("z_bh.pak.ps2", StringComparison.OrdinalIgnoreCase)) > 0,
            "base-zone paks must pool after mission-local sources");
    }
}
