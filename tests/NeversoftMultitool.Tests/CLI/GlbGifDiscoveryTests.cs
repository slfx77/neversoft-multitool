using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GlbGifDiscoveryTests
{
    [Fact]
    public void SelectCandidatePaths_FiltersExtensionCaseInsensitivelyAndRemovesOnlyExactDuplicates()
    {
        var glbPath = Path.Combine("input", "nested", "first.GlB");
        var caseDistinctGlbPath = Path.Combine("input", "nested", "FIRST.GLB");
        var unrelatedPath = Path.Combine("input", "nested", "notes.txt");

        var result = GlbGifCommand.SelectCandidatePaths(
            [glbPath, glbPath, caseDistinctGlbPath, unrelatedPath]);

        Assert.Equal([glbPath, caseDistinctGlbPath], result);
    }

    [Fact]
    public void FindDuplicateBesideSourceOutputs_UsesExactDirectoryAndStemIdentity()
    {
        var left = Path.Combine("input", "left");
        var right = Path.Combine("input", "right");
        var lowerCasePath = Path.Combine(left, "clip.glb");
        var sameStemPath = Path.Combine(left, "clip.GLB");
        var upperCasePath = Path.Combine(left, "CLIP.GLB");
        var otherDirectoryPath = Path.Combine(right, "clip.GLB");

        Assert.Equal(
            [Path.GetFullPath(Path.Combine(left, "clip"))],
            GlbGifCommand.FindDuplicateBesideSourceOutputs(
                [lowerCasePath, sameStemPath]));
        Assert.Empty(GlbGifCommand.FindDuplicateBesideSourceOutputs(
            [lowerCasePath, upperCasePath]));
        Assert.Empty(GlbGifCommand.FindDuplicateBesideSourceOutputs(
            [lowerCasePath, otherDirectoryPath]));
    }
}
