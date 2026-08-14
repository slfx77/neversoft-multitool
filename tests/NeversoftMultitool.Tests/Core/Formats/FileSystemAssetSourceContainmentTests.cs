using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool.Tests.Core.Formats;

public sealed class FileSystemAssetSourceContainmentTests
{
    [Fact]
    public void CompanionLookups_RejectPathsOutsideSourceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-asset-source-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var outsidePath = Path.Combine(root, "outside.bin");
        var injectedDirectory = Path.Combine(root, "secret");
        var injectedPath = Path.Combine(injectedDirectory, "target.tex");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(injectedDirectory);
            File.WriteAllBytes(outsidePath, [0x10, 0x20]);
            File.WriteAllBytes(injectedPath, [0x30, 0x40]);
            var source = new FileSystemAssetSource(Path.Combine(sourceDirectory, "model.psx"));
            var traversalName = $"..{Path.DirectorySeparatorChar}outside.bin";
            var traversalStem = $"..{Path.DirectorySeparatorChar}outside";
            var injectedSubdir = Path.Combine("source", "..", "secret");

            Assert.False(source.CompanionExists(traversalName));
            Assert.Null(source.TryReadCompanion(traversalName));
            Assert.Null(source.TryResolveCompanionPath(traversalName));
            Assert.False(source.CompanionExists(outsidePath));
            Assert.Null(source.TryReadCompanion(outsidePath));
            Assert.Null(source.TryResolveCompanionPath(outsidePath));
            Assert.Null(source.TryReadCompanion(traversalStem, [".bin"]));
            Assert.Null(source.TryResolveCompanionPath(traversalStem, [".bin"]));
            Assert.Null(source.TryReadCompanion("outside", [".bin"], ["."]));
            Assert.Null(source.TryResolveCompanionPath("outside", [".bin"], ["."]));
            Assert.Null(source.TryReadCompanion("target", [".tex"], [injectedSubdir]));
            Assert.Null(source.TryResolveCompanionPath("target", [".tex"], [injectedSubdir]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompanionLookups_ExactBasename_RemainsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-asset-source-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "Models");
        var sourcePath = Path.Combine(sourceDirectory, "model.psx");
        var companionPath = Path.Combine(sourceDirectory, "inside.bin");
        var siblingDirectory = Path.Combine(root, "Textures");
        var siblingPath = Path.Combine(siblingDirectory, "sibling.tex");
        byte[] companionBytes = [0x30, 0x40];
        byte[] siblingBytes = [0x50, 0x60];

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(siblingDirectory);
            File.WriteAllBytes(companionPath, companionBytes);
            File.WriteAllBytes(siblingPath, siblingBytes);
            var source = new FileSystemAssetSource(sourcePath);

            Assert.True(source.CompanionExists("inside.bin"));
            Assert.Equal(companionBytes, source.TryReadCompanion("inside.bin"));
            Assert.Equal(companionPath, source.TryResolveCompanionPath("inside.bin"));
            Assert.Equal(companionBytes, source.TryReadCompanion("inside", [".bin"]));
            Assert.Equal(companionPath, source.TryResolveCompanionPath("inside", [".bin"]));
            Assert.Equal(siblingBytes,
                source.TryReadCompanion("sibling", [".tex"], [".", "Textures"]));
            Assert.Equal(siblingPath,
                source.TryResolveCompanionPath("sibling", [".tex"], [".", "Textures"]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
