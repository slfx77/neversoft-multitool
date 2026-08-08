using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the carved bundle names end to end (2026-08-08).
///     <para>
///         Names come primarily from the TRIGGERS, which spell the files they
///         load; the content dictionary is the fallback for characters and props
///         no trigger mentions. The two are complementary — the triggers reach
///         the <c>_l</c> texture libraries, which carve as stubs with no content
///         to key on, and the dictionary reaches everything a level never loads.
///     </para>
/// </summary>
public sealed class N64BundleNameResolverTests(TestPaths paths)
{
    public static TheoryData<string, string, int, int> Coverage() => new()
    {
        // build, rom, minimum named, total model slots.
        // Measured 2026-08-08. Content identity alone reached 329 of 594
        // overall and could never reach a stub; with the triggers it is 416.
        { "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64", 73, 80 },
        { "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64", 98, 141 },
        { "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64", 66, 112 },
        { "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 179, 261 }
    };

    [CorpusTheory]
    [MemberData(nameof(Coverage))]
    public void EveryRom_NamesTheMeasuredShareOfItsSlots(
        string build, string rom, int minimumNamed, int expectedSlots)
    {
        var shells = CarveShells(build, rom);
        Assert.Equal(expectedSlots, shells.Count);

        var named = shells.Count(static path => IsNamed(path));
        Assert.True(named >= minimumNamed,
            $"{build}: named {named}/{shells.Count}, expected at least {minimumNamed}");
    }

    /// <summary>
    ///     The regression guard for the run alignment. THPS1's skdown family
    ///     carries BOTH an <c>_l</c> and an <c>l2</c> library, which is exactly
    ///     the pair that swaps under the wrong sort order — with
    ///     OrdinalIgnoreCase these four slots came back unnamed.
    ///     <para>
    ///         Slots 002/007/009 are also the proof that triggers reach what
    ///         content cannot: all three are 24-byte stubs, so the dictionary is
    ///         structurally blind to them.
    ///     </para>
    /// </summary>
    [CorpusFact]
    public void Thps1_NamesTheFullSkDownRunIncludingItsStubLibraries()
    {
        var shells = CarveShells(
            "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["002"] = "SkVans_L",   // stub
            ["004"] = "SkDown",
            ["005"] = "SkDown_2",
            ["006"] = "SkDown_H",
            ["007"] = "SkDown_L",   // stub
            ["008"] = "SkDown_O",
            ["009"] = "SkDownL2"    // stub; sorts AFTER SkDown_O, not before
        };

        foreach (var (slot, name) in expected)
        {
            var path = shells.Single(p => p.StartsWith($"models/{slot}/", StringComparison.Ordinal));
            Assert.Equal($"models/{slot}/{slot}_{name}.psx.n64", path);
        }
    }

    /// <summary>
    ///     A name is only ever added to the slot, never substituted for it, so
    ///     the games' own asset-id space stays readable off a file list and two
    ///     bundles holding identical content still get distinct paths.
    /// </summary>
    [CorpusFact]
    public void EveryCarvedBundlePathKeepsItsSlotPrefix()
    {
        var shells = CarveShells(
            "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64");

        Assert.Equal(shells.Count, shells.Distinct(StringComparer.Ordinal).Count());
        foreach (var path in shells)
        {
            var slot = path.Split('/')[1];
            var file = path[(path.LastIndexOf('/') + 1)..];
            Assert.True(file.StartsWith(slot, StringComparison.Ordinal),
                $"{path} does not lead with its slot");
        }
    }

    private static bool IsNamed(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        // "045.psx.n64" is unnamed; "008_c_kart.psx.n64" is named.
        return file.IndexOf('_') >= 0;
    }

    private List<string> CarveShells(string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        return assets
            .Select(static a => a.Path)
            .Where(static p => p.StartsWith("models/", StringComparison.Ordinal)
                               && p.EndsWith(".psx.n64", StringComparison.Ordinal))
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();
    }
}
