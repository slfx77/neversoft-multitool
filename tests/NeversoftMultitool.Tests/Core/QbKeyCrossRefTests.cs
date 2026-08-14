using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.QbKey;

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

    [CorpusTheory]
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

    [CorpusTheory]
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
        Assert.Null(EnumerateBytes([0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void EnumerateAllHashes_RecognizedMagicOnly_ReturnsNull()
    {
        Assert.Null(EnumerateBytes([0x04, 0x00, 0x02, 0x00]));
    }

    [Fact]
    public void EnumerateAllHashes_TruncatedDeclaredObjectRecord_ReturnsNull()
    {
        var data = new byte[12];
        data[0] = 0x04;
        data[2] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);

        Assert.Null(EnumerateBytes(data));
    }

    [Fact]
    public void EnumerateAllHashes_MetadataPointerBeforeMeshTable_ReturnsNull()
    {
        var data = CreateEmptyPsxFixture(36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0);

        Assert.Null(EnumerateBytes(data));
    }

    [Fact]
    public void EnumerateAllHashes_TruncatedExtendedNameRecord_ReturnsNull()
    {
        var data = CreateEmptyPsxFixture(40);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 1);

        Assert.Null(EnumerateBytes(data));
    }

    [Fact]
    public void EnumerateAllHashes_ExtendedHeaderMissingActualCount_ReturnsNull()
    {
        var data = CreateEmptyPsxFixture(44);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0xFFFFFFFF);

        Assert.Null(EnumerateBytes(data));
    }

    [Fact]
    public void EnumerateAllHashes_DeclaredTextureMissingTopPointer_ReturnsNull()
    {
        var data = CreateEmptyPsxFixture(36);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 1);

        Assert.Null(EnumerateBytes(data));
    }

    [Fact]
    public void EnumerateAllHashes_ExactEmptyFile_ReturnsEmptyHashes()
    {
        var data = CreateEmptyPsxFixture(36);

        var result = EnumerateBytes(data);

        Assert.NotNull(result);
        Assert.Empty(result.MeshNameHashes);
        Assert.Empty(result.TextureNameHashes);
        Assert.Null(result.DetailTextureNames);
        Assert.Null(result.CubemapNames);
    }

    private static byte[] CreateEmptyPsxFixture(int length)
    {
        var data = new byte[length];
        data[0] = 0x04;
        data[2] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0xFFFFFFFF);
        return data;
    }

    private static PsxHashEnumeration? EnumerateBytes(byte[] data)
    {
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"NsMultitool_Test_Hashes_{Guid.NewGuid():N}.psx");
        try
        {
            File.WriteAllBytes(tempFile, data);
            return PsxHashEnumerator.EnumerateAllHashes(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
