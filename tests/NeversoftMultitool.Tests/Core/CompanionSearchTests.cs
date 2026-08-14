using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class CompanionSearchTests
{
    [Fact]
    public void IsSameOrDescendant_CaseOnlyDifference_RespectsComparison()
    {
        var ancestor = Path.Combine("root", "Game");
        var caseDistinctPath = Path.Combine("root", "game", "child");

        Assert.False(CompanionSearch.IsSameOrDescendant(
            ancestor, caseDistinctPath, StringComparison.Ordinal));
        Assert.True(CompanionSearch.IsSameOrDescendant(
            ancestor, caseDistinctPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCommonRoot_CaseDistinctLinuxDirectories_IsOrderIndependent()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Case-distinct directory semantics are Linux-specific");

        var root = Path.Combine(Path.GetTempPath(), $"nmt-companion-case-root-{Guid.NewGuid():N}");
        var upperGame = Path.Combine(root, "Game");
        var lowerGame = Path.Combine(root, "game");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Models"));
            Directory.CreateDirectory(Path.Combine(root, "Textures"));
            Directory.CreateDirectory(Path.Combine(upperGame, "Models"));
            Directory.CreateDirectory(Path.Combine(upperGame, "Textures"));
            Directory.CreateDirectory(lowerGame);

            var upperFile = Path.Combine(upperGame, "a.geom.ps2");
            var lowerFile = Path.Combine(lowerGame, "b.geom.ps2");

            Assert.Equal(root, CompanionSearch.GetCommonRoot([upperFile, lowerFile]));
            Assert.Equal(root, CompanionSearch.GetCommonRoot([lowerFile, upperFile]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GetCommonRoot_SimilarlyNamedSiblingDirectories_IsOrderIndependent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-companion-root-{Guid.NewGuid():N}");
        var game = Path.Combine(root, "Game");
        var gameBackup = Path.Combine(root, "GameBackup");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Models"));
            Directory.CreateDirectory(Path.Combine(root, "Textures"));
            Directory.CreateDirectory(Path.Combine(game, "Models"));
            Directory.CreateDirectory(Path.Combine(game, "Textures"));
            Directory.CreateDirectory(gameBackup);

            var gameFile = Path.Combine(game, "a.geom.ps2");
            var backupFile = Path.Combine(gameBackup, "b.geom.ps2");

            Assert.Equal(root, CompanionSearch.GetCommonRoot([gameFile, backupFile]));
            Assert.Equal(root, CompanionSearch.GetCommonRoot([backupFile, gameFile]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
