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
    // Re-pinned 2026-08-03 for the three-part detection change, measured with
    // `PsxAnalyzer overlay-census`. Per-file delta is the sum of three moves.
    // ADDS from IsExactTwin now also requiring identical APPEARANCE, because
    // coincident same-texture twins whose colours or UVs differ — the baked
    // light-and-shadow duplicates, the corpus' biggest unseparated class —
    // now flag. ADDS from the small-decal branch using the exact
    // clipped-shared-area rule instead of centroid-inside, catching partial
    // overlaps where neither centroid lands inside its partner. REMOVALS of
    // back-to-back SINGLE-SIDED pairs — opposing raw normals in one
    // canonical-plane bucket — excluded everywhere because backface culling
    // already separates them, making their old flags needless splits.
    //
    // Spider-Man: the decal-heavy start rooftop, the demo level's sign panels,
    // and l7a2_g whose glass sheets drove the lift-orientation fix.
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "l2a1_g.psx", 77)] // 82: −5 back-to-back
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "lda1_g.psx", 5)] // 18: −13 back-to-back
    [InlineData("Spider-Man (2000-9-1, PSX - Final)", "l7a2_g.psx", 593)] // 538: +55 appearance twins
    // THPS2 Dreamcast: the reported water (SKB2), the baked light/shadow floor
    // duplicates (SKMAR), and the fence/sprite level (SKPH).
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKB2.PSX", 24)] // 1: +23 exact-area decals (its whole unseparated class)
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKMAR.PSX", 101)] // 102: −1 back-to-back
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", "SKPH.PSX", 27)] // 29: −2 back-to-back
    // THPS2 PS1 + THPS1: skware's baked bright/shadow wall duplicates (the
    // o117f9/o151f9 exemplar) and skmall's storefronts, which are mostly
    // back-to-back single-sided walls the old rule flagged needlessly.
    [InlineData("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "skware.psx", 106)] // 37: +69 appearance twins
    [InlineData("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "skmar.psx", 37)] // 39: −2 back-to-back
    [InlineData("Tony Hawk's Pro Skater (1999-9-29, PSX - Final)", "skmall.psx", 7)] // 47: −40 back-to-back
    public void Find_FlagsThePinnedOverlayCount(string buildName, string fileName, int expected)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{buildName}/{fileName} sample not available");

        var file = PsxMeshFile.Parse(path!);
        Assert.NotNull(file);

        Assert.Equal(expected, PsxCoplanarOverlayDetector.Find(file!).Count);
    }
}
