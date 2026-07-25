using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Locks the name-matching semantics of the PSH bone remap to the
///     decomp-verified CalculateAnimOrder contract (byte-PERFECT 2026-07-09):
///     case-sensitive comparison first, lowest-index bone wins on duplicate
///     names. The case-insensitive tier is converter leniency beyond the
///     engine and must only engage when the engine-faithful tier misses.
/// </summary>
public sealed class PsxAnimationBoneMapTests
{
    private static PshFile MakePsh(params (string Name, int Index)[] bones)
    {
        var list = bones
            .Select(b => new PshBone { Name = b.Name, Index = b.Index, ParentName = null })
            .ToList();
        return new PshFile(list) { Bones = list };
    }

    [Fact]
    public void TryBuild_ExactCaseMatch_PreferredOverCaseInsensitive()
    {
        // Engine tier is a real strcmp: "Head" must bind to "Head" (index 1),
        // not to the case-insensitive first encounter "head" (index 0).
        var source = MakePsh(("Head", 0), ("head", 1));
        var target = MakePsh(("head", 0), ("Head", 1));

        var ok = PsxAnimationBoneMap.TryBuild(source, target, 2, out var map, out var diagnostic);

        Assert.True(ok, diagnostic);
        Assert.Equal(1, map[0]);
        Assert.Equal(0, map[1]);
    }

    [Fact]
    public void TryBuild_DuplicateTargetNames_LowestIndexWins()
    {
        // CalculateAnimOrder's inner loop breaks on the first hit, so the
        // lowest-index duplicate wins. A last-wins regression would resolve
        // to index 1, which is out of range for boneCount 1 and would fail.
        var source = MakePsh(("arm", 0));
        var target = MakePsh(("arm", 0), ("arm", 1));

        var ok = PsxAnimationBoneMap.TryBuild(source, target, 1, out var map, out var diagnostic);

        Assert.True(ok, diagnostic);
        Assert.Equal(0, map[0]);
    }

    [Fact]
    public void TryBuild_CaseInsensitiveFallback_StillMatches()
    {
        // Converter leniency: with no exact-case candidate, the
        // case-insensitive tier may bind (the engine itself would leave the
        // slot unmapped here).
        var source = MakePsh(("HEAD", 0));
        var target = MakePsh(("head", 0));

        var ok = PsxAnimationBoneMap.TryBuild(source, target, 1, out var map, out var diagnostic);

        Assert.True(ok, diagnostic);
        Assert.Equal(0, map[0]);
    }

    [Fact]
    public void TryBuild_UnmatchedSourceBone_FailsWithDiagnostic()
    {
        // The engine leaves unmatched slots stale (never initialized); the
        // converter cannot reproduce runtime staleness, so it refuses the
        // remap outright rather than guessing (never maps to bone 0).
        var source = MakePsh(("head", 0), ("tail", 1));
        var target = MakePsh(("head", 0), ("torso", 1));

        var ok = PsxAnimationBoneMap.TryBuild(source, target, 2, out _, out var diagnostic);

        Assert.False(ok);
        Assert.Contains("tail", diagnostic);
    }
}