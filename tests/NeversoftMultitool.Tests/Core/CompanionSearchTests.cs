using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class CompanionSearchTests
{
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
