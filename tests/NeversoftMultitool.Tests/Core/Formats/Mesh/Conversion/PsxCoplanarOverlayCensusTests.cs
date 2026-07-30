using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins <see cref="PsxCoplanarOverlayDetector" />'s per-file overlay count
///     across a representative slice of the corpus.
///
///     Every prior change to this detector moved output on files no assertion
///     covered, so the moves were invisible: the semi-transparent early-out took
///     SKB2 from 23 flags to 1 and skware from 34 to 37 with the suite fully
///     green, and the near-equal branch's interior-overlap rule moved several
///     more. The l2a1_g / l1a2_g node pins alone cannot see any of it — both
///     files are insensitive to the semi-transparent and near-equal branches.
///     Measured 2026-07-30 with `PsxAnalyzer overlay-census`, which reports the
///     same set this asserts; a count that moves here is not necessarily wrong,
///     but it must be explained and re-pinned deliberately rather than silently.
/// </summary>
public sealed class PsxCoplanarOverlayCensusTests(TestPaths paths)
{
    [CorpusTheory]
    // Spider-Man: the decal-heavy start rooftop, the demo level's sign panels,
    // and l7a2_g whose glass sheets drove the lift-orientation fix.
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "l2a1_g.psx", 82)]
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "lda1_g.psx", 18)]
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "l7a2_g.psx", 538)]
    // THPS2 Dreamcast: the reported water (SKB2), the baked light/shadow floor
    // duplicates (SKMAR), and the fence/sprite level (SKPH).
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKB2.PSX", 1)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKMAR.PSX", 102)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKPH.PSX", 29)]
    // THPS2 PS1 + THPS1: skware's double-sided walls and the partial overlaps the
    // centroid-inside rule used to drop (skmall).
    [InlineData("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "skware.psx", 37)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "skmar.psx", 39)]
    [InlineData("Tony Hawk's Pro Skater (1999-9-29, PSX - Final)", "skmall.psx", 47)]
    public void Find_FlagsThePinnedOverlayCount(string buildName, string fileName, int expected)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{buildName}/{fileName} sample not available");

        var file = PsxMeshFile.Parse(path!);
        Assert.NotNull(file);

        Assert.Equal(expected, PsxCoplanarOverlayDetector.Find(file!).Count);
    }
}
