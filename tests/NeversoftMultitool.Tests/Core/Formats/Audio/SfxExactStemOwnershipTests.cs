using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxExactStemOwnershipTests
{
    [Theory]
    [InlineData(24, 32, true)]
    [InlineData(25, int.MaxValue, false)]
    [InlineData(0, 7, false)]
    [InlineData(0, 8, true)]
    public void AliasConfidence_UsesInclusiveScoreAndMarginBoundaries(
        int bestScore,
        int secondBestScore,
        bool expected)
    {
        Assert.Equal(expected, SfxAliasResolver.IsHighConfidenceMatch(bestScore, secondBestScore));
    }

    [Fact]
    public void Plan_KatAndVabShareKey_EverySheetIsOwnedByKat()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/demo.vab"),
            new(1, "sounds/demo.sfx"),
            new(2, "sounds/demo.kat"),
            new(3, "sounds/DEMO.SFX")
        ];

        var ownership = SfxExactStemOwnership.Plan(assets);

        Assert.Equal([new SfxCueOwnership(3, 2), new SfxCueOwnership(1, 2)], ownership);
    }

    [Fact]
    public void Plan_NoKat_UsesUniqueVab()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "levels/demo.sfx"),
            new(1, "levels/demo.vab")
        ];

        Assert.Equal(
            [new SfxCueOwnership(0, 1)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_SameStemInDifferentDirectory_RemainsUnowned()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "female/demo.kat"),
            new(1, "male/demo.sfx")
        ];

        Assert.Equal(
            [new SfxCueOwnership(1, null)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_MixedCaseAndSeparators_UseOneDirectoryIdentity()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "SOUNDS\\Demo.SFX"),
            new(1, "sounds/demo.KAT")
        ];

        Assert.Equal(
            [new SfxCueOwnership(0, 1)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_DuplicatePreferredBanks_AreAmbiguousEvenWithUniqueVab()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/demo.sfx"),
            new(1, "sounds/demo.kat"),
            new(2, "sounds/DEMO.KAT"),
            new(3, "sounds/demo.vab")
        ];

        Assert.Equal(
            [new SfxCueOwnership(0, null)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_OutputOrderUsesFullPathRatherThanInputOrder()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "z/last.sfx"),
            new(1, "z/last.kat"),
            new(2, "a/first.sfx"),
            new(3, "a/first.kat")
        ];

        Assert.Equal(
            [new SfxCueOwnership(2, 3), new SfxCueOwnership(0, 1)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_UnownedSheetWithUniqueHighConfidenceAnchor_UsesAnchoredBank()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0, 2]);
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/base.kat"),
            new(1, "sounds/base.sfx", matchingCues),
            new(2, "sounds/alternate.sfx", matchingCues)
        ];

        Assert.Equal(
            [new SfxCueOwnership(2, 0, true), new SfxCueOwnership(1, 0)],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_ArchiveFullPaths_KeepAliasOwnershipInsideVirtualDirectory()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0]);
        SfxScanCandidate[] assets =
        [
            new(0, "characters/hero/base.kat"),
            new(1, "characters/hero/base.sfx", matchingCues),
            new(2, "characters/hero/voice.sfx", matchingCues),
            new(3, "characters/villain/voice.sfx", matchingCues)
        ];

        Assert.Equal(
            [
                new SfxCueOwnership(1, 0),
                new SfxCueOwnership(2, 0, true),
                new SfxCueOwnership(3, null)
            ],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_AliasScoreAboveThreshold_RemainsUnowned()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/base.kat"),
            new(1, "sounds/base.sfx", SfxTestBuilder.CreateSfx([0, 1, 2])),
            new(2, "sounds/orphan.sfx", SfxTestBuilder.CreateSfx([0]))
        ];

        Assert.Contains(
            new SfxCueOwnership(2, null),
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_EquallyScoredDistinctBanks_RefusesAliasTie()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0]);
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/alpha.kat"),
            new(1, "sounds/alpha.sfx", matchingCues),
            new(2, "sounds/beta.kat"),
            new(3, "sounds/beta.sfx", matchingCues),
            new(4, "sounds/orphan.sfx", matchingCues)
        ];

        Assert.Contains(
            new SfxCueOwnership(4, null),
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_MultipleAnchorsForOneBank_UseBestScoreWithoutFalseTie()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/base.kat"),
            new(1, "sounds/base.sfx", SfxTestBuilder.CreateSfx([1])),
            new(2, "sounds/BASE.SFX", SfxTestBuilder.CreateSfx([0])),
            new(3, "sounds/orphan.sfx", SfxTestBuilder.CreateSfx([0]))
        ];

        Assert.Contains(
            new SfxCueOwnership(3, 0, true),
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_RunnerUpWithinMargin_RefusesAlias()
    {
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/alpha.kat"),
            new(1, "sounds/alpha.sfx", SfxTestBuilder.CreateSfx([0])),
            new(2, "sounds/beta.kat"),
            new(3, "sounds/beta.sfx", SfxTestBuilder.CreateSfx([1])),
            new(4, "sounds/orphan.sfx", SfxTestBuilder.CreateSfx([0]))
        ];

        Assert.Contains(
            new SfxCueOwnership(4, null),
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_AmbiguousExactBanks_CannotEscapeToCrossStemAlias()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0]);
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/anchor.kat"),
            new(1, "sounds/anchor.sfx", matchingCues),
            new(2, "sounds/demo.kat"),
            new(3, "sounds/DEMO.KAT"),
            new(4, "sounds/demo.sfx", matchingCues)
        ];

        Assert.Contains(
            new SfxCueOwnership(4, null),
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_MalformedSheets_DoNotEstablishOrReceiveAliasOwnership()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0]);
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/base.kat"),
            new(1, "sounds/base.sfx", [0x12]),
            new(2, "sounds/orphan.sfx", matchingCues),
            new(3, "sounds/malformed.sfx", [0x34])
        ];

        Assert.Equal(
            [
                new SfxCueOwnership(1, 0),
                new SfxCueOwnership(3, null),
                new SfxCueOwnership(2, null)
            ],
            SfxExactStemOwnership.Plan(assets));
    }

    [Fact]
    public void Plan_AliasOutputOrderIsDeterministicFromFullPath()
    {
        var matchingCues = SfxTestBuilder.CreateSfx([0]);
        SfxScanCandidate[] assets =
        [
            new(0, "sounds/z_orphan.sfx", matchingCues),
            new(1, "sounds/a_anchor.sfx", matchingCues),
            new(2, "sounds/a_anchor.kat")
        ];

        Assert.Equal(
            [new SfxCueOwnership(1, 2), new SfxCueOwnership(0, 2, true)],
            SfxExactStemOwnership.Plan(assets));
    }
}
