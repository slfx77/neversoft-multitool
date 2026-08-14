using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class BuildTreeCompanionLocatorTests
{
    private const string Stem = "School";
    private const string TextureExtension = ".tex.ps2";

    [Fact]
    public void ConflictingNearestFallback_DoesNotFallThroughToOuterCanonical()
    {
        using var temp = new TempDirectory();
        var innerRoot = Path.Combine(temp.Path, "Project");
        var source = CreateSource(innerRoot);

        WriteFile(
            Path.Combine(innerRoot, "pre", Stem + "Scn", "variant_a", Stem + TextureExtension),
            [0xA1]);
        WriteFile(
            Path.Combine(innerRoot, "pre", Stem + "Scn", "variant_b", Stem + TextureExtension),
            [0xB2]);
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "Levels", Stem, Stem + TextureExtension),
            [0xC3]);

        var result = BuildTreeCompanionLocator.TryReadTextureCompanion(
            source,
            Stem,
            [TextureExtension]);

        Assert.Null(result);
    }

    [Fact]
    public void UniqueRecursiveFallback_ReturnsBytes()
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp.Path);
        byte[] expected = [0x10, 0x20, 0x30];
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "textures", Stem + TextureExtension),
            expected);

        var result = BuildTreeCompanionLocator.TryReadTextureCompanion(
            source,
            Stem,
            [TextureExtension]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanonicalDirectMatch_WinsOverConflictingRecursiveMatches()
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp.Path);
        byte[] expected = [0xCA, 0xFE];
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "Levels", Stem, Stem + TextureExtension),
            expected);
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "variant_a", Stem + TextureExtension),
            [0xA1]);
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "variant_b", Stem + TextureExtension),
            [0xB2]);

        var result = BuildTreeCompanionLocator.TryReadTextureCompanion(
            source,
            Stem,
            [TextureExtension]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ByteIdenticalRecursiveMatches_AreAccepted()
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp.Path);
        byte[] expected = [0x44, 0x55, 0x66];
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "variant_a", Stem + TextureExtension),
            expected);
        WriteFile(
            Path.Combine(temp.Path, "pre", Stem + "Scn", "variant_b", Stem + TextureExtension),
            expected);

        var result = BuildTreeCompanionLocator.TryReadTextureCompanion(
            source,
            Stem,
            [TextureExtension]);

        Assert.Equal(expected, result);
    }

    private static FileSystemAssetSource CreateSource(string buildRoot)
    {
        var assetPath = Path.Combine(buildRoot, "Levels", Stem, Stem + ".geom.ps2");
        WriteFile(assetPath, [0x47]);
        return new FileSystemAssetSource(assetPath);
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtBuildTree_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
