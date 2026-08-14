using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the world-scale predicate the viewer's camera mode keys on
///     (2026-08-07).
///     <para>
///         The feature is <c>bounds.bin</c>'s largest per-mesh radius. World
///         content and character/prop models separate with an EMPTY BAND —
///         character/prop tops out at 1129 and world starts at 1298 — so the
///         1213 threshold is the band's midpoint rather than a tuned constant,
///         and any value inside the band scores identically.
///     </para>
///     <para>
///         The band is asserted over ALL non-empty bundles, not the 328 that
///         carry a PS1-Rosetta label. That distinction is load-bearing: the
///         labelled subset reported a wider 1045..1298 band, but THPS3 slot 169
///         (a single-object model, unnamed) sits at 1115 and Spider-Man reaches
///         1129 — a test written to the subset's numbers fails on the real
///         corpus, which is how the narrower band was found.
///     </para>
/// </summary>
public sealed class N64BundleClassifierTests(TestPaths paths)
{
    [Theory]
    // radius, objectCount, expected
    [InlineData(0f, 0, N64BundleClass.Empty)]           // authored-empty stub
    [InlineData(5000f, 0, N64BundleClass.Empty)]        // objectCount wins: nothing to place
    [InlineData(1129f, 40, N64BundleClass.CharacterProp)]  // the measured charprop ceiling
    [InlineData(1212f, 400, N64BundleClass.CharacterProp)] // still below the band midpoint
    [InlineData(1213f, 24, N64BundleClass.Level)]       // exactly at the threshold
    [InlineData(1298f, 400, N64BundleClass.Level)]      // l8a5_g.psx, the level floor
    [InlineData(1298f, 23, N64BundleClass.ObjectBank)]  // world-scale but too few objects
    [InlineData(9000f, 1, N64BundleClass.ObjectBank)]   // a bank can be huge and sparse
    public void Classify_SplitsOnTheMeasuredBoundaries(
        float radius, int objectCount, N64BundleClass expected)
    {
        Assert.Equal(expected, N64BundleClassifier.Classify(radius, objectCount));
    }

    /// <summary>
    ///     The camera predicate deliberately ignores object count: a level and
    ///     its object bank are both world content and both want Fly. Only the
    ///     walk eye height needs the finer split, because a bank has no floor.
    /// </summary>
    [Fact]
    public void IsWorldScale_CoversBothLevelsAndBanks()
    {
        Assert.True(N64BundleClassifier.IsWorldScale(1298f));
        Assert.True(N64BundleClassifier.IsWorldScale(9000f));
        Assert.False(N64BundleClassifier.IsWorldScale(1129f));

        // A bank flies but is not a level.
        Assert.True(N64BundleClassifier.IsWorldScale(5000f));
        Assert.False(N64BundleClassifier.IsLevel(5000f, 3));
        Assert.True(N64BundleClassifier.IsLevel(5000f, 300));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteRadius_NeverClassifiesAsWorld(float radius)
    {
        Assert.False(N64BundleClassifier.IsWorldScale(radius));
        Assert.False(N64BundleClassifier.IsLevel(radius, N64BundleClassifier.LevelMinObjectCount));
        Assert.Equal(
            N64BundleClass.CharacterProp,
            N64BundleClassifier.Classify(radius, N64BundleClassifier.LevelMinObjectCount));

        Assert.True(N64BundleClassifier.IsWorldScale(N64BundleClassifier.WorldScaleRadius));
    }

    /// <summary>
    ///     The empty band is the whole basis of the threshold, so assert it
    ///     directly against the corpus: no bundle may sit between the measured
    ///     character/prop ceiling and level floor near the cut. A regression in
    ///     <c>bounds.bin</c> parsing would fill the band with garbage values.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64")]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64")]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64")]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64")]
    public void EveryRom_LeavesTheBandEmpty(string build, string rom)
    {
        var radii = BundleRadii(build, rom);
        Assert.NotEmpty(radii);

        // 1129..1298 is the measured band, taken over ALL non-empty bundles
        // rather than the labelled subset. Nothing may land strictly inside it.
        var inside = radii.Where(static r => r is > 1129f and < 1298f).ToList();
        Assert.True(inside.Count == 0,
            $"{build}: {inside.Count} bundles inside the empty band: "
            + string.Join(", ", inside.Take(5).Select(static r => r.ToString("F1"))));
    }

    /// <summary>
    ///     Both classes must actually be present, or "the band is empty" would
    ///     pass trivially on a ROM whose bounds all read zero.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64", 32, 33)]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64", 57, 59)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64", 57, 30)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 95, 87)]
    public void EveryRom_SplitsIntoTheMeasuredPopulations(
        string build, string rom, int expectedWorld, int expectedCharacterProp)
    {
        var radii = BundleRadii(build, rom);
        Assert.Equal(expectedWorld, radii.Count(N64BundleClassifier.IsWorldScale));
        Assert.Equal(expectedCharacterProp, radii.Count(static r => !N64BundleClassifier.IsWorldScale(r)));
    }

    /// <summary>
    ///     Max bounding radius per non-empty bundle, straight out of the ROM.
    /// </summary>
    private List<float> BundleRadii(string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        var bounds = assets
            .Where(static a => a.Path.StartsWith("models/", StringComparison.Ordinal)
                               && a.Path.EndsWith("/bounds.bin", StringComparison.Ordinal))
            .ToDictionary(static a => a.Path[..a.Path.LastIndexOf('/')], static a => a.Data);

        var radii = new List<float>();
        foreach (var asset in assets)
        {
            if (!asset.Path.StartsWith("models/", StringComparison.Ordinal)
                || !asset.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            {
                continue;
            }

            // Authored-empty stubs carry no geometry and no bounds; they are not
            // a class, they are an absence.
            if (PsxN64ShellFile.Parse(asset.Data) == null)
                continue;

            var directory = asset.Path[..asset.Path.LastIndexOf('/')];
            if (bounds.TryGetValue(directory, out var data))
                radii.Add(N64BoundsFile.MaxRadius(data));
        }

        return radii;
    }
}
