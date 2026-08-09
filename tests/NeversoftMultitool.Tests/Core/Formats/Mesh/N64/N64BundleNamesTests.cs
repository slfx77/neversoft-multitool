using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the carved-bundle name dictionary (2026-08-07).
///     <para>
///         The N64 ports re-encoded Neversoft's PS1 <c>.psx</c> containers, so a
///         bundle's mesh-name-hash SET identifies the PS1 file it came from. The
///         PS1 corpus is not available at runtime, so the 2026-08-07 offline
///         corpus match is materialized into an embedded dictionary keyed by a
///         digest of that set.
///     </para>
///     <para>
///         The digest is the load-bearing contract: if its implementation changes,
///         the embedded dictionary silently resolves nothing. <see cref="ComputeKey_MatchesReferenceVector" />
///         pins the independently established digest for a known hash set.
///     </para>
/// </summary>
public sealed class N64BundleNamesTests(TestPaths paths)
{
    /// <summary>
    ///     Reference digest for the set { 0x00010000, 0x00020000 }. If a change
    ///     moves this, the embedded table no longer matches the runtime key.
    /// </summary>
    private const ulong ReferenceVector = 0x79938094ABD6A2EA;

    [Fact]
    public void ComputeKey_MatchesReferenceVector()
    {
        Assert.Equal(ReferenceVector, N64BundleNames.ComputeKey([0x0001_0000, 0x0002_0000]));
    }

    /// <summary>
    ///     A shell's hashes are read in object-table order, which need not match
    ///     the harvest's; and a mesh may be named more than once. Both must key
    ///     identically or the dictionary would be order-dependent.
    /// </summary>
    [Fact]
    public void ComputeKey_IgnoresOrderAndDuplicates()
    {
        Assert.Equal(ReferenceVector,
            N64BundleNames.ComputeKey([0x0002_0000, 0x0001_0000, 0x0001_0000]));
    }

    /// <summary>
    ///     Values below the minimum are ordinals and sentinels, not name hashes.
    ///     The harvest filters them identically; a divergence here would shift
    ///     every key.
    /// </summary>
    [Fact]
    public void ComputeKey_IgnoresHashesBelowTheMinimum()
    {
        Assert.Equal(ReferenceVector,
            N64BundleNames.ComputeKey([0x0001_0000, 0x0000_0005, 0x0002_0000]));
        Assert.Null(N64BundleNames.ComputeKey([0x0000_0005]));
        Assert.Null(N64BundleNames.ComputeKey([]));
    }

    [Fact]
    public void Dictionary_LoadsFromTheEmbeddedResource()
    {
        Assert.True(N64BundleNames.Count > 900,
            $"expected the full harvested table, got {N64BundleNames.Count} keys");
    }

    /// <summary>
    ///     One unambiguous key, spelled out, so a regenerated table that changed
    ///     shape fails here rather than silently degrading coverage.
    /// </summary>
    [Fact]
    public void TryResolve_NamesAKnownBundle()
    {
        Assert.Equal("skss", N64BundleNames.TryResolve(0x006180C0ED5A4975));
    }

    /// <summary>
    ///     Content identity is genuinely one-of-N in places. Where the
    ///     candidates share a stem that is ITSELF one of them, that stem is a
    ///     name every candidate agrees on: <c>skss_o</c> and <c>skss_o2</c> are
    ///     the one- and two-player object banks of the same level.
    /// </summary>
    [Fact]
    public void TryResolve_UsesASharedStemWhenEveryCandidateAgreesOnIt()
    {
        Assert.Equal(["skss_o", "skss_o2"], N64BundleNames.ResolveAll(0x001A135A20CCF311));
        Assert.Equal("skss_o", N64BundleNames.TryResolve(0x001A135A20CCF311));

        // glif | glif2 | glif2b | glif_fe — one skater's model set.
        Assert.Equal("glif", N64BundleNames.TryResolve(0x545973AADD4D3E81));
    }

    /// <summary>
    ///     The refusal that keeps names honest. Characters built on a shared rig
    ///     are INDISTINGUISHABLE by content — every THPS2 skater carries the same
    ///     19 part names and the same part positions — so one key covers nine
    ///     unrelated files. Returning the first would name more bundles at the
    ///     cost of asserting something false, so the bundle keeps its slot.
    /// </summary>
    [Fact]
    public void TryResolve_RefusesToPickAmongUnrelatedCandidates()
    {
        var skaters = N64BundleNames.ResolveAll(0xFEA7ED5AFFEE27DF);
        Assert.Contains("hawk", skaters);
        Assert.Contains("secret1", skaters);
        Assert.Null(N64BundleNames.TryResolve(0xFEA7ED5AFFEE27DF));

        // l9a3_o | l9a4_o | lba3_o | lba4_o — four different levels' banks that
        // happen to hold the same four meshes. "l" is not a name.
        Assert.Null(N64BundleNames.TryResolve(0xF777611E317A2538));
    }

    /// <summary>
    ///     A shared prefix must BE a candidate, never merely a prefix, or the
    ///     rule would invent names nobody has — <c>c_bus</c> + <c>c_bull</c>
    ///     would otherwise yield <c>c_bu</c>. 21 keys are in that shape.
    /// </summary>
    [Fact]
    public void TryResolve_NeverInventsAPrefixThatIsNotItselfACandidate()
    {
        foreach (var key in new ulong[] { 0xFEA7ED5AFFEE27DF, 0xF777611E317A2538 })
        {
            var resolved = N64BundleNames.TryResolve(key);
            if (resolved != null)
                Assert.Contains(resolved, N64BundleNames.ResolveAll(key));
        }
    }

    [Fact]
    public void ResolveAll_IsEmptyForAnUnknownKey()
    {
        Assert.Empty(N64BundleNames.ResolveAll(0xDEAD_BEEF_DEAD_BEEF));
        Assert.Null(N64BundleNames.TryResolve(0xDEAD_BEEF_DEAD_BEEF));
    }

    /// <summary>A stub slot and a garbage buffer both degrade to null, never throw.</summary>
    [Fact]
    public void TryResolveShell_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(N64BundleNames.TryResolveShell([]));
        Assert.Null(N64BundleNames.TryResolveShell([0x00, 0x02, 0x00, 0x04]));
        Assert.Null(N64BundleNames.TryResolveShell(new byte[64]));
    }

    public static TheoryData<string, string, int, int> RomCoverage() => new()
    {
        // ROM build, file, minimum resolved, parsed shells.
        // Measured 2026-08-07. "Resolved" counts only names that are TRUE of the
        // content — 433 of 450 bundles match some PS1 file, but 104 of those
        // matches are shared-rig characters no content key can separate.
        { "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64", 59, 65 },
        { "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64", 78, 116 },
        { "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64", 63, 87 },
        { "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 129, 182 }
    };

    /// <summary>
    ///     Coverage against the real carts. These are floors, not equalities: a
    ///     better harvest should raise them, and only a regression can lower
    ///     them. 329/450 overall when this was written.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(RomCoverage))]
    public void EveryRom_ResolvesTheMeasuredShareOfItsBundles(
        string build, string rom, int minimumResolved, int expectedParsed)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        var parsed = 0;
        var resolved = 0;
        foreach (var asset in assets)
        {
            if (!asset.Path.StartsWith("models/", StringComparison.Ordinal)
                || !asset.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            {
                continue;
            }

            var name = N64BundleNames.TryResolveShell(asset.Data);
            // A stub slot parses to nothing; only count shells with content.
            if (PsxN64ShellFileHasContent(asset.Data))
                parsed++;
            if (name != null)
                resolved++;
        }

        Assert.Equal(expectedParsed, parsed);
        Assert.True(resolved >= minimumResolved,
            $"{build}: resolved {resolved}/{parsed}, expected at least {minimumResolved}");
    }

    private static bool PsxN64ShellFileHasContent(byte[] data)
    {
        var shell = NeversoftMultitool.Core.Formats.Mesh.Psx.PsxN64ShellFile.Parse(data);
        return shell is { MeshNameHashes.Length: > 0 };
    }
}
