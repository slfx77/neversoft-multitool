using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.CLI;

/// <summary>
///     Xbox 360 / PS3 animation routing. THAW uses its ordinary big-endian
///     v0x28 format; Project 8 and Proving Ground use the later 0x20-wrapped,
///     section-addressed revision. All share the compound suffix discovery and
///     compression-table lookup routes pinned here.
/// </summary>
public class SkaNextGenRoutingTests(TestPaths paths)
{
    private const string ThawX360 = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";

    [Theory]
    [InlineData("CAM_1.ska.xen", true)]
    [InlineData("cutscene.ska.ps3", true)]
    [InlineData("anim.ska.ngc", true)]
    [InlineData("anim.ska.ps2", true)]
    [InlineData("anim.ska", true)]
    [InlineData("anim.ske.xen", false)]
    [InlineData("notes.txt", false)]
    public void IsAnimFileName_CoversTheNextGenSuffixes(string name, bool expected)
    {
        Assert.Equal(expected, AnimationDiscovery.IsAnimFileName(name));
    }

    /// <summary>
    ///     The compressed clips fail with "requires a T48 compression table"
    ///     unless the shared table is found, so this pins the lookup reaching
    ///     the Xbox 360 layout by walking up to the build root.
    /// </summary>
    [CorpusFact]
    public void FindCompressTable_LocatesTheXboxThreeSixtyTable()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ska = paths.FindSampleFile(ThawX360, "CAM_1.ska.xen");
        Assert.SkipWhen(ska == null, "THAW X360 cutscene animations not present");

        Assert.NotNull(SkaCommand.FindCompressTable(ska!));
    }

    /// <summary>
    ///     A whole cutscene directory: 182 clips, every one parsing. Before the
    ///     table lookup reached <c>data/anims</c>, 60 of these failed while the
    ///     other 122 parsed — a partial failure that a single-file test misses.
    /// </summary>
    [CorpusFact]
    public void ThawX360CutsceneDirectory_ParsesEveryAnimation()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sample = paths.FindSampleFile(ThawX360, "CAM_1.ska.xen");
        Assert.SkipWhen(sample == null, "THAW X360 cutscene animations not present");

        // The count is per-cutscene and not worth pinning; the invariant that
        // matters is that NONE fail. Before the table lookup reached
        // data/anims, roughly a third of any such directory failed.
        var directory = Path.GetDirectoryName(sample!)!;
        var files = Directory.GetFiles(directory, "*.ska.xen");
        Assert.True(files.Length > 100, $"Expected a full cutscene directory, found {files.Length}");

        var parsed = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var animation = SkaFile.Parse(File.ReadAllBytes(file), SkaCommand.FindCompressTable(file));
                if (animation.BoneTracks.Length > 0) parsed++;
                else failures.Add($"{Path.GetFileName(file)}: no bone tracks");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures.Take(5))}");
        Assert.Equal(files.Length, parsed);
    }
}
