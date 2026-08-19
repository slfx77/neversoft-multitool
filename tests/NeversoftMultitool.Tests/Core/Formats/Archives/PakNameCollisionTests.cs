using System.Text.RegularExpressions;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Mission worldzone paks stamp one shared name CRC (0x6F980DC3 → "mission")
///     on every unnamed entry, so pre-2026-08-19 name generation collided and
///     extraction silently overwrote siblings (m_zhogaps13_gameplay lost 13 of
///     22 unnamed entries on disk). These pins hold the collision rule: every
///     pak enumerates with unique names, colliding groups carry their offset
///     suffix, and non-colliding paks (all zone paks) keep their names verbatim.
/// </summary>
public sealed partial class PakNameCollisionTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [GeneratedRegex("_[0-9A-F]{8}(_\\d+)?\\.", RegexOptions.None)]
    private static partial Regex DisambiguationSuffix();

    [CorpusFact]
    public void MissionPak_EnumeratesLosslesslyWithUniqueNames()
    {
        var pakPath = paths.FindSampleFile(BuildName, "m_zhogaps13_gameplay.pak.ps2");
        Assert.SkipWhen(pakPath is null, "m_zhogaps13_gameplay.pak.ps2 not found");

        var typed = PakArchive.GetTypedEntries(pakPath!);
        Assert.Equal(25, typed.Count);
        Assert.Equal(25, typed.Select(static t => $"{t.Entry.Directory}/{t.Entry.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The two previously-shadowed classes: both MDLs survive, all seven SKAs survive.
        Assert.Equal(2, typed.Count(static t => t.Entry.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(7, typed.Count(static t => t.Entry.Name.EndsWith(".ska", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, typed.Count(static t => t.Entry.Name.EndsWith(".stex", StringComparison.OrdinalIgnoreCase)));

        // Both enumeration paths must agree on names (companion resolution uses
        // typed entries; extraction uses the flat list).
        var flat = PakArchive.GetFileList(pakPath!);
        Assert.Equal(
            typed.Select(static t => t.Entry.FullName).OrderBy(static n => n, StringComparer.OrdinalIgnoreCase),
            flat.Select(static e => e.FullName).OrderBy(static n => n, StringComparer.OrdinalIgnoreCase));
    }

    [CorpusFact]
    public void ZonePaks_KeepTheirNamesVerbatim()
    {
        var pakPath = paths.FindSampleFile(BuildName, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath is null, "z_bh.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.NotEmpty(entries);
        Assert.All(entries, static e =>
            Assert.False(DisambiguationSuffix().IsMatch(e.Name),
                $"zone pak entry '{e.Name}' should not carry a collision suffix"));
    }

    [CorpusFact]
    public void AllThawPaks_EnumerateWithUniqueNames()
    {
        var paks = paths.FindSampleFiles(BuildName, "*.pak.ps2")
            .Where(static p => PakArchive.IsPakArchive(p))
            .ToList();
        Assert.SkipWhen(paks.Count == 0, "THAW PS2 pak corpus not available");

        var collidingPaks = 0;
        foreach (var pak in paks)
        {
            var entries = PakArchive.GetFileList(pak);
            var names = entries.Select(static e => $"{e.Directory}/{e.Name}").ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            if (entries.Any(static e => DisambiguationSuffix().IsMatch(e.Name)))
                collidingPaks++;
        }

        // Collisions are the mission-pak shape; they must exist in this corpus
        // (else the rule is dead code) and stay a small minority.
        Assert.True(collidingPaks > 0, "expected the mission paks to exercise the collision rule");
        Assert.True(collidingPaks < paks.Count / 4,
            $"{collidingPaks}/{paks.Count} paks carry collision suffixes — the rule is over-firing");
    }
}
