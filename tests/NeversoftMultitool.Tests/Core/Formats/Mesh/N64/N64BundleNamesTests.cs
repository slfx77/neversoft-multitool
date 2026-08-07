using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the carved-bundle name dictionary (2026-08-07).
///     <para>
///         The N64 ports re-encoded Neversoft's PS1 <c>.psx</c> containers, so a
///         bundle's mesh-name-hash SET identifies the PS1 file it came from. The
///         PS1 corpus is not available at runtime, so the identity is harvested
///         offline by <c>tools/utilities/harvest_n64_bundle_names.py</c> into an
///         embedded dictionary keyed by a digest of that set.
///     </para>
///     <para>
///         The digest is the load-bearing contract: it is computed independently
///         in Python (harvest) and C# (runtime), and if the two ever disagree the
///         dictionary silently resolves nothing. <see cref="ComputeKey_MatchesTheHarvestSelfTestVector" />
///         pins the exact literal that <c>--selftest</c> prints, which is the only
///         thing tying the two implementations together.
///     </para>
/// </summary>
public sealed class N64BundleNamesTests(TestPaths paths)
{
    /// <summary>
    ///     The value <c>harvest_n64_bundle_names.py --selftest</c> prints. If a
    ///     change moves this, the embedded table must be regenerated in the same
    ///     commit or every lookup starts missing.
    /// </summary>
    private const ulong SelfTestVector = 0x79938094ABD6A2EA;

    [Fact]
    public void ComputeKey_MatchesTheHarvestSelfTestVector()
    {
        Assert.Equal(SelfTestVector, N64BundleNames.ComputeKey([0x0001_0000, 0x0002_0000]));
    }

    /// <summary>
    ///     A shell's hashes are read in object-table order, which need not match
    ///     the harvest's; and a mesh may be named more than once. Both must key
    ///     identically or the dictionary would be order-dependent.
    /// </summary>
    [Fact]
    public void ComputeKey_IgnoresOrderAndDuplicates()
    {
        Assert.Equal(SelfTestVector,
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
        Assert.Equal(SelfTestVector,
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
    ///     Content identity is genuinely one-of-N in places: <c>skss_o</c> and
    ///     <c>skss_o2</c> are the one- and two-player object banks and hold the
    ///     same meshes. Both names are true of the content, so both are kept —
    ///     <see cref="N64BundleNames.TryResolve" /> takes the first and the
    ///     carver's slot prefix keeps the emitted path unique regardless.
    /// </summary>
    [Fact]
    public void ResolveAll_ExposesEveryCandidateForAnAmbiguousKey()
    {
        var all = N64BundleNames.ResolveAll(0x001A135A20CCF311);
        Assert.Equal(["skss_o", "skss_o2"], all);
        Assert.Equal("skss_o", N64BundleNames.TryResolve(0x001A135A20CCF311));
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
        // Measured 2026-08-07 by the harvest's own coverage report.
        { "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64", 65, 65 },
        { "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64", 108, 116 },
        { "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64", 84, 87 },
        { "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 176, 182 }
    };

    /// <summary>
    ///     Coverage against the real carts. These are floors, not equalities: a
    ///     better harvest should raise them, and only a regression can lower
    ///     them. 433/450 overall (96.2%) when this was written.
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
