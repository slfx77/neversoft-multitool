using NeversoftMultitool.Core.Formats.Mesh.Detection;
using LegacyKind = NeversoftMultitool.Tests.Core.Formats.Mesh.Detection.LegacyMeshRoutingReference.LegacyKind;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Detection;

/// <summary>
///     Pins <c>MeshTypeDetector</c> against <see cref="LegacyMeshRoutingReference" />:
///     every suffix that reached a parser before the five extension lists were
///     folded together must still reach the same one.
/// </summary>
public class MeshTypeDetectorParityTests
{
    public static TheoryData<string, LegacyKind> RoutedSuffixes()
    {
        var data = new TheoryData<string, LegacyKind>();
        foreach (var (suffix, kind) in LegacyMeshRoutingReference.RoutedSuffixes())
            data.Add(suffix, kind);
        return data;
    }

    public static TheoryData<string> AllLegacySuffixes()
    {
        var data = new TheoryData<string>();
        foreach (var (suffix, _) in LegacyMeshRoutingReference.RoutedSuffixes())
            data.Add(suffix);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoutedSuffixes))]
    public void DetectByName_MatchesLegacyRouting_ForEveryPreExistingSuffix(string suffix, LegacyKind expected)
    {
        var route = MeshTypeDetector.DetectByName("asset" + suffix);

        // A bare .skin/.mdl legitimately has no kind until content is read.
        if (expected == LegacyKind.AmbiguousScene)
        {
            Assert.Equal(MeshFileKind.None, route.Kind);
            Assert.True(route.RequiresContentProbe);
            return;
        }

        Assert.Equal(Enum.Parse<MeshFileKind>(expected.ToString()), route.Kind);
        Assert.Equal(suffix, route.Suffix);
    }

    [Theory]
    [MemberData(nameof(AllLegacySuffixes))]
    public void IsMeshCandidate_MatchesLegacyCliGate(string suffix)
    {
        var name = "asset" + suffix;
        var legacy = LegacyMeshRoutingReference.CommandIsPotentialMeshFile(name);
        var current = MeshTypeDetector.IsMeshCandidate(name) || MeshTypeDetector.IsWorldzoneCandidate(name);
        Assert.Equal(legacy, current);
    }

    [Theory]
    [MemberData(nameof(AllLegacySuffixes))]
    public void ScanCandidateGate_MatchesLegacyScannerGate(string suffix)
    {
        // Deliberate divergence: the legacy GUI scanner's own COL list omitted
        // ".col.psp", so the tab silently ignored PSP collision files that the CLI
        // (FormatProbeMesh and MeshCommand both list it) accepted. Unifying gives
        // the GUI the CLI's coverage. Zero .col.psp files exist in Sample/Builds,
        // so nothing in the corpus changes; see ScanCandidateGate_NowAcceptsColPsp.
        if (string.Equals(suffix, ".col.psp", StringComparison.Ordinal))
            return;

        var name = "asset" + suffix;
        var legacy = LegacyMeshRoutingReference.ScannerIsScanCandidate(name);
        var current = MeshTypeDetector.IsMeshCandidate(name) && !MeshTypeDetector.IsObjectDdm(name);
        Assert.Equal(legacy, current);
    }

    [Fact]
    public void ScanCandidateGate_NowAcceptsColPsp_ClosingACliGuiGap()
    {
        Assert.False(LegacyMeshRoutingReference.ScannerIsScanCandidate("asset.col.psp"));
        Assert.True(LegacyMeshRoutingReference.CommandIsPotentialMeshFile("asset.col.psp"));
        Assert.True(MeshTypeDetector.IsMeshCandidate("asset.col.psp"));
    }

    [Fact]
    public void ScanCandidateGate_ExcludesObjectDdm_LikeLegacy()
    {
        Assert.False(LegacyMeshRoutingReference.ScannerIsScanCandidate("level_o.ddm"));
        Assert.True(MeshTypeDetector.IsObjectDdm("level_o.ddm"));
        Assert.False(MeshTypeDetector.IsObjectDdm("level.ddm"));
    }

    [Fact]
    public void IsMeshCandidate_ExcludesWorldzone_SoArchiveWalksKeepNesting()
    {
        // The archive walker relies on .pak.ps2 NOT being a generic candidate so the
        // entry falls through to nested-archive opening.
        Assert.False(MeshTypeDetector.IsMeshCandidate("z_bh.pak.ps2"));
        Assert.True(MeshTypeDetector.IsWorldzoneCandidate("z_bh.pak.ps2"));
    }

    [Theory]
    [MemberData(nameof(AllLegacySuffixes))]
    public void GetStem_MatchesLegacyStripHelpers(string suffix)
    {
        // Deliberate divergence: ".psx.n64" was in no legacy strip list, so the
        // generic helper left a trailing ".psx" behind ("asset.psx"). That value
        // was never used — both the CLI and the GUI name N64 bundles from their
        // parent directory ("n64_<NNN>"), since every bundle file is called
        // geometry.psx.n64. See GetStem_StripsTheWholeN64Suffix.
        if (string.Equals(suffix, ".psx.n64", StringComparison.Ordinal))
            return;

        var name = "asset" + suffix;
        var expected = suffix switch
        {
            ".col.xbx" or ".col.wpc" or ".col.ps2" or ".col.psp" =>
                LegacyMeshRoutingReference.StripColExtension(name),
            _ => LegacyMeshRoutingReference.StripCompoundSuffix(
                name, LegacyMeshRoutingReference.ScannerCompoundExtensions)
        };

        Assert.Equal(expected, MeshTypeDetector.GetStem(name));
    }

    [Fact]
    public void GetStem_StripsTheWholeN64Suffix()
    {
        Assert.Equal(
            "asset.psx",
            LegacyMeshRoutingReference.StripCompoundSuffix(
                "asset.psx.n64", LegacyMeshRoutingReference.ScannerCompoundExtensions));
        Assert.Equal("asset", MeshTypeDetector.GetStem("asset.psx.n64"));
    }

    [Fact]
    public void GetStem_PrefersCompoundOverBare()
    {
        Assert.Equal("hawk", MeshTypeDetector.GetStem("hawk.iskin.ps2"));
        Assert.Equal("hawk", MeshTypeDetector.GetStem("hawk.skin.ps2"));
        Assert.Equal("hawk", MeshTypeDetector.GetStem("hawk.skin"));
        Assert.Equal("Arrow", MeshTypeDetector.GetStem("Arrow.col.xbx"));
        Assert.Equal("mission", MeshTypeDetector.GetStem("mission.col"));
    }

    [Fact]
    public void MatchSuffix_PrefersLongestKnownSuffix()
    {
        Assert.Equal(".iskin.ps2", MeshTypeDetector.MatchSuffix("a.iskin.ps2"));
        Assert.Equal(".skin.ps2", MeshTypeDetector.MatchSuffix("a.skin.ps2"));
        Assert.Equal(".psx.n64", MeshTypeDetector.MatchSuffix("geometry.psx.n64"));
        Assert.Null(MeshTypeDetector.MatchSuffix("readme.txt"));
    }

    [Fact]
    public void KnownSuffixes_AreDistinct()
    {
        Assert.Equal(
            MeshTypeDetector.KnownSuffixes.Length,
            MeshTypeDetector.KnownSuffixes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IsMeshCandidate_UnrelatedExtension_ReturnsFalse()
    {
        Assert.False(MeshTypeDetector.IsMeshCandidate("song.wav"));
        Assert.False(MeshTypeDetector.IsMeshCandidate("script.qb"));
        Assert.False(MeshTypeDetector.IsMeshCandidate("texture.tex.ps2"));
    }
}
