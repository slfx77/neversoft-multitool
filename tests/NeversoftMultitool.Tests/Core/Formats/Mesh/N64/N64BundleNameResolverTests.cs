using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Mesh.N64;

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
        // overall and could never reach a stub; family alignment plus the two
        // uniquely disambiguated outsider literals bring the total to 418.
        { "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64", 73, 80 },
        { "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64", 98, 141 },
        { "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64", 66, 112 },
        { "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 178, 261 }
    };

    [Fact]
    public void OutsiderLiteralDisambiguation_RequiresAOneToOneRemainingAssignment()
    {
        var identities = new N64BundleNameResolver.ContentIdentity?[]
        {
            new(1, ["level", "actor"]),
            new(1, ["level", "actor"]),
            new(2, ["shared", "other"]),
            new(2, ["shared", "other"]),
            new(3, ["used", "fresh"]),
            new(4, ["twice", "left"]),
            new(5, ["twice", "right"])
        };
        string?[] current = ["level", null, null, null, "used", null, null];
        var outsiders = new HashSet<string>(
            ["ACTOR", "shared", "fresh", "twice"], StringComparer.OrdinalIgnoreCase);

        var selected = N64BundleNameResolver.SelectUniqueOutsiderNames(
            identities, current, outsiders);

        Assert.Equal(new Dictionary<int, string> { [1] = "actor" }, selected);
    }

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

    /// <summary>
    ///     Spider-Man has four ambiguous content keys touched by outsider TRG
    ///     literals. DEM4_G leaves exactly one unresolved slot after
    ///     authoritative family alignment, so it is provable. The rejected
    ///     L1A2a family leaves Jameson/JJJViewer ambiguous at both slots 014 and
    ///     168; the Mysterio and firering pairs are likewise indistinguishable.
    ///     Every ambiguous occurrence must retain its numeric name.
    /// </summary>
    [CorpusFact]
    public void SpiderMan_DisambiguatesOnlyTheRemainingUniqueOutsiderLiteralSlot()
    {
        var shells = CarveShells(
            "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64");

        Assert.Contains("models/014/014.psx.n64", shells);
        Assert.Contains("models/168/168.psx.n64", shells);
        Assert.Contains("models/254/254_dem4_g.psx.n64", shells);
        Assert.Contains("models/110/110.psx.n64", shells);
        Assert.Contains("models/224/224.psx.n64", shells);
        Assert.Contains("models/166/166.psx.n64", shells);
        Assert.Contains("models/226/226.psx.n64", shells);
        // 179, not 178: content naming keys on the mesh-name hashes the shell
        // parser produces, so while the parser was rejecting 87 of the 450 real
        // shells one library lost the anchor its family alignment needed. Every
        // slot asserted above is unchanged either way.
        Assert.Equal(179, shells.Count(IsNamed));
    }

    /// <summary>
    ///     Regression for the reported shifted L1A2 family. The old placement
    ///     called geometry slot 168 <c>L1A2_O</c> and put the library name
    ///     <c>L1A2a_L</c> on geometry. A rejected family run must stay numeric,
    ///     while every emitted terminal <c>_L</c> name remains an authored-empty
    ///     shell rather than merely a 24-byte malformed buffer.
    /// </summary>
    [CorpusFact]
    public void SpiderMan_L1A2ShiftedRunFailsClosedAndEveryNamedLibraryIsAuthoredEmpty()
    {
        var assets = CarveModelAssets(
            "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64");

        var slot168 = Assert.Single(assets, static asset =>
            asset.Path.StartsWith("models/168/", StringComparison.Ordinal));
        Assert.Equal("models/168/168.psx.n64", slot168.Path);
        Assert.DoesNotContain(assets, static asset =>
            asset.Path.EndsWith("168_L1A2_O.psx.n64", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(assets, static asset =>
            asset.Path == "models/169/169_l1a2a_g.psx.n64");
        var slot170 = Assert.Single(assets, static asset =>
            asset.Path.StartsWith("models/170/", StringComparison.Ordinal));
        Assert.Equal("models/170/170.psx.n64", slot170.Path);
        Assert.False(N64BundleNameResolver.IsStub(slot170.Data));
        Assert.DoesNotContain(assets, static asset =>
            asset.Path.Contains("L1A2a_L", StringComparison.OrdinalIgnoreCase));

        var slot014 = Assert.Single(assets, static asset =>
            asset.Path.StartsWith("models/014/", StringComparison.Ordinal));
        Assert.Equal("models/014/014.psx.n64", slot014.Path);
        var slot014Identity = N64BundleNames.TryReadShellIdentity(slot014.Data);
        var slot168Identity = N64BundleNames.TryReadShellIdentity(slot168.Data);
        Assert.NotNull(slot014Identity);
        Assert.NotNull(slot168Identity);
        Assert.Equal(slot014Identity.Value.Key, slot168Identity.Value.Key);
        Assert.Equal(["jameson", "jjviewer"], slot168Identity.Value.Candidates);

        var libraries = assets.Where(static asset =>
        {
            var file = asset.Path[(asset.Path.LastIndexOf('/') + 1)..];
            var stem = file[..^".psx.n64".Length];
            return stem.EndsWith("_L", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        Assert.NotEmpty(libraries);
        Assert.All(libraries, static asset =>
            Assert.True(N64BundleNameResolver.IsStub(asset.Data),
                $"{asset.Path} is named as a texture library but is not an authored-empty shell."));
    }

    private static bool IsNamed(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        // "045.psx.n64" is unnamed; "008_c_kart.psx.n64" is named.
        return file.IndexOf('_') >= 0;
    }

    private List<string> CarveShells(string build, string rom)
    {
        return CarveModelAssets(build, rom)
            .Select(static asset => asset.Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();
    }

    private List<N64AssetCarver.CarvedAsset> CarveModelAssets(string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        return assets
            .Where(static asset => asset.Path.StartsWith("models/", StringComparison.Ordinal)
                                   && asset.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            .OrderBy(static asset => asset.Path, StringComparer.Ordinal)
            .ToList();
    }
}
