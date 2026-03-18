using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core;

public class QbKeyCrossRefTests(TestPaths paths)
{
    [Theory]
    [InlineData("wall01", 0x42ED71EEu)]
    [InlineData("floor", 0x41BA29D1u)]
    [InlineData("ground", 0x58007C97u)]
    [InlineData("board", 0xA7A9D4B8u)]
    [InlineData("blood", 0x40EE3A40u)]
    public void QbKeyHash_KnownNames_ProduceExpectedHashes(string name, uint expectedHash)
    {
        Assert.Equal(expectedHash, QbKey.Hash(name));
    }

    [Fact]
    public void QbKeyHash_IsCaseSensitive()
    {
        // PS1-era Neversoft games use case-sensitive CRC-32 (no lowercasing)
        Assert.NotEqual(QbKey.Hash("Wall01"), QbKey.Hash("wall01"));
        Assert.NotEqual(QbKey.Hash("FLOOR"), QbKey.Hash("floor"));
    }

    [Fact]
    public void QbKeyHashLower_IsCaseInsensitive()
    {
        // THUG+ era uses lowercase normalization
        Assert.Equal(QbKey.HashLower("Wall01"), QbKey.HashLower("wall01"));
        Assert.Equal(QbKey.HashLower("FLOOR"), QbKey.HashLower("floor"));
    }

    [Theory]
    [InlineData("Anl_MBF_PitBull.png", 0xB90A3A81u)]
    [InlineData("Anl_MBF_PitBull_Chain.png", 0x877B7B3Fu)]
    [InlineData("Body_M_Torso.png", 0xFBA05359u)]
    public void QbKeyResolve_LoadsGameCubeTextureNameMappings(string name, uint expectedHash)
    {
        Assert.Equal(expectedHash, QbKey.HashLower(name));
        Assert.Equal(name, QbKey.TryResolve(expectedHash));
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void EnumerateAllHashes_ReturnsNonEmptyMeshAndTextureHashes(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var hashes = PsxHashEnumerator.EnumerateAllHashes(inputFile);

        Assert.NotNull(hashes);
        Assert.NotNull(hashes.MeshNameHashes);
        Assert.True(hashes.TextureNameHashes.Length > 0, "No texture name hashes found");
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void EnumerateAllHashes_TextureHashesMatchEnumerateTextures(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var allHashes = PsxHashEnumerator.EnumerateAllHashes(inputFile);
        var textures = PsxLibrary.EnumerateTextures(inputFile);

        Assert.NotNull(allHashes);
        Assert.NotEmpty(textures);

        // Texture name hashes from EnumerateAllHashes should match those from EnumerateTextures
        // Order may differ because EnumerateTextures uses header.Index lookup while
        // EnumerateAllHashes returns the raw hash array order
        var textureHashes = textures.Select(t => t.NameHash).Where(h => h != 0).ToHashSet();
        var allTextureHashes = allHashes.TextureNameHashes.Where(h => h != 0).ToHashSet();

        Assert.Equal(allTextureHashes.Count, textureHashes.Count);
        Assert.True(allTextureHashes.SetEquals(textureHashes),
            "Texture hashes from EnumerateAllHashes and EnumerateTextures should contain the same values");
    }

    [Fact]
    public void EnumerateAllHashes_InvalidFile_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_HashInvalid_" + Guid.NewGuid().ToString("N")[..8]);
        var tempFile = Path.Combine(tempDir, "invalid.psx");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00]);

            var result = PsxHashEnumerator.EnumerateAllHashes(tempFile);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}