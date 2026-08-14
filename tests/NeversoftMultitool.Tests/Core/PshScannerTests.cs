using NeversoftMultitool.Core;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core;

public sealed class PshScannerTests
{
    [Fact]
    public void ScanPshNames_UppercaseOnlyHash_MatchesOriginalDefineCase()
    {
        const string uppercaseName = "HAWK_PELVIS";

        var result = ScanPsh(
            "#define PART_HAWK_PELVIS 0",
            uppercaseName);

        var match = Assert.Single(result.Matches);
        Assert.Equal(uppercaseName, match.Name);
        Assert.Equal(QbKey.Hash(uppercaseName), match.Hash);
        Assert.Equal("psh-scan", match.SourceFile);
        Assert.Equal(QbKeyMappingSource.PshPartName, match.Source);
        Assert.Equal(1, result.TotalPshFiles);
        Assert.Equal(2, result.TotalCandidateNames);
        Assert.Equal(1, result.TotalMeshHashes);
    }

    [Fact]
    public void ScanPshNames_ParentName_RemainsAuthoritativeCaseForLowerKey()
    {
        const string parentName = "Hawk_Pelvis";
        const string psh = """
                           #define PART_HAWK_PELVIS 0
                           //   parent: Hawk_Pelvis
                           """;

        var result = ScanPsh(psh, parentName);

        var match = Assert.Single(result.Matches);
        Assert.Equal(parentName, match.Name);
        Assert.Equal(QbKey.Hash(parentName), match.Hash);
        Assert.Equal(2, result.TotalCandidateNames);
    }

    [Fact]
    public void ScanPshNames_ParentNameMatchingDefineCase_IsNotCountedTwice()
    {
        const string uppercaseName = "HAWK_PELVIS";
        const string psh = """
                           #define PART_HAWK_PELVIS 0
                           //   parent: HAWK_PELVIS
                           """;

        var result = ScanPsh(psh, uppercaseName);

        var match = Assert.Single(result.Matches);
        Assert.Equal(uppercaseName, match.Name);
        Assert.Equal(1, result.TotalCandidateNames);
    }

    private static PshScanResult ScanPsh(string contents, params string[] meshNames)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NsMtPshScanner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "parts.psh"), contents);
            return PshScanner.ScanPshNames(
                directory,
                meshNames.Select(QbKey.Hash).ToHashSet());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
