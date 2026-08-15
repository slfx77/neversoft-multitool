using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxN64ShellTailBoundaryTests
{
    private const int MetadataOffset = 52;
    private const uint MeshNameHash = 0x11223344;

    [Fact]
    public void Parse_DoesNotInventMissingMeshNameHashAndTextureCount()
    {
        var data = CreateShellPrefix(56);
        WriteUInt32(data, MetadataOffset, uint.MaxValue);

        Assert.Null(PsxN64ShellFile.Parse(data));
    }

    [Fact]
    public void Parse_CompactTailPadsOnlyTheMissingTextureHashValue()
    {
        var data = CreateCompactShell();

        var shell = PsxN64ShellFile.Parse(data);

        Assert.NotNull(shell);
        Assert.Equal([MeshNameHash], shell!.MeshNameHashes);
        Assert.Equal([0u], shell.TextureHashes);
    }

    /// <summary>
    ///     The texture-hash count is the number of distinct textures and is not
    ///     tied to the mesh count. Across the 2,620 parseable PS1 files this
    ///     container is a byteswapped copy of, the two agree in only 210; among
    ///     the 450 real N64 shells, 54 disagree. Each shape below is one the
    ///     carved corpus actually contains.
    /// </summary>
    [Theory]
    [InlineData(0u, 0)]   // 36 shells: no textures at all
    [InlineData(1u, 4)]   // the mesh count, which is only a coincidence
    [InlineData(3u, 12)]  // 18 shells: more textures than meshes
    public void Parse_AcceptsAnyTextureCountAndPadsTheStrippedValues(
        uint textureCount, int expectedValueBytes)
    {
        var data = CreateCompactShell(textureCount);

        var shell = PsxN64ShellFile.Parse(data);

        Assert.NotNull(shell);
        Assert.Equal([MeshNameHash], shell!.MeshNameHashes);
        Assert.Equal(expectedValueBytes / sizeof(uint), shell.TextureHashes.Length);
        Assert.All(shell.TextureHashes, hash => Assert.Equal(0u, hash));
    }

    [Fact]
    public void Parse_ShellEndingAtTheMeshNameHashes_ReadsAZeroTextureCount()
    {
        // 33 of the 450 real shells stop here: the carve cut the count word off
        // as well as its values.
        var data = CreateShellPrefix(MetadataOffset + 8);
        WriteUInt32(data, MetadataOffset, uint.MaxValue);
        WriteUInt32(data, MetadataOffset + 4, MeshNameHash);

        var shell = PsxN64ShellFile.Parse(data);

        Assert.NotNull(shell);
        Assert.Equal([MeshNameHash], shell!.MeshNameHashes);
        Assert.Empty(shell.TextureHashes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parse_PartiallyPresentTextureCount_IsRefusedRatherThanAssembled(int presentBytes)
    {
        // Every carved shell's trailing region is 4-byte aligned, so a count
        // word split across the physical end never occurs. Refuse it instead of
        // building a count out of real bytes plus padding.
        var data = CreateShellPrefix(MetadataOffset + 8 + presentBytes);
        WriteUInt32(data, MetadataOffset, uint.MaxValue);
        WriteUInt32(data, MetadataOffset + 4, MeshNameHash);

        Assert.Null(PsxN64ShellFile.Parse(data));
    }

    [Theory]
    [InlineData(48u)]
    [InlineData(64u)]
    [InlineData(0x80000000u)]
    public void Parse_MetadataOffsetMustAddressThePhysicalTail(uint metadataOffset)
    {
        var data = CreateCompactShell();
        WriteUInt32(data, 4, metadataOffset);

        Assert.Null(PsxN64ShellFile.Parse(data));
    }

    [Fact]
    public void Parse_RequiresAPhysicalTaggedChainTerminator()
    {
        var data = CreateShellPrefix(60);
        WriteUInt32(data, MetadataOffset, 0x12345678);
        WriteUInt32(data, MetadataOffset + 4, 0);

        Assert.Null(PsxN64ShellFile.Parse(data));
    }

    [Fact]
    public void Parse_RejectsATaggedChunkWhosePayloadIsTruncated()
    {
        var data = CreateShellPrefix(61);
        WriteUInt32(data, MetadataOffset, 0x12345678);
        WriteUInt32(data, MetadataOffset + 4, 2);
        data[60] = 0xAA;

        Assert.Null(PsxN64ShellFile.Parse(data));
    }

    [Fact]
    public void Parse_TaggedChunkLimitIsInclusiveAndFailClosed()
    {
        Assert.NotNull(PsxN64ShellFile.Parse(CreateChunkedShell(16)));
        Assert.Null(PsxN64ShellFile.Parse(CreateChunkedShell(17)));
    }

    [Fact]
    public void Parse_PhysicalTextureValueIsPreservedAndSuffixIsIgnored()
    {
        const uint textureHash = 0xAABBCCDD;
        var data = CreateCompactShell(length: 72);
        WriteUInt32(data, 64, textureHash);
        WriteUInt32(data, 68, 0xDEADBEEF);

        var shell = PsxN64ShellFile.Parse(data);

        Assert.NotNull(shell);
        Assert.Equal([MeshNameHash], shell!.MeshNameHashes);
        Assert.Equal([textureHash], shell.TextureHashes);
    }

    private static byte[] CreateCompactShell(uint textureCount = 1, int length = 64)
    {
        var data = CreateShellPrefix(length);
        WriteUInt32(data, MetadataOffset, uint.MaxValue);
        WriteUInt32(data, MetadataOffset + 4, MeshNameHash);
        WriteUInt32(data, MetadataOffset + 8, textureCount);
        return data;
    }

    private static byte[] CreateChunkedShell(int chunkCount)
    {
        var data = CreateShellPrefix(MetadataOffset + chunkCount * 8 + 12);
        var cursor = MetadataOffset;
        for (var i = 0; i < chunkCount; i++)
        {
            WriteUInt32(data, cursor, checked(0x10000000u + (uint)i));
            WriteUInt32(data, cursor + 4, 0);
            cursor += 8;
        }

        WriteUInt32(data, cursor, uint.MaxValue);
        WriteUInt32(data, cursor + 4, MeshNameHash);
        WriteUInt32(data, cursor + 8, 1);
        return data;
    }

    private static byte[] CreateShellPrefix(int length)
    {
        var data = new byte[length];
        WriteUInt32(data, 0, 0x00020004);
        WriteUInt32(data, 4, MetadataOffset);
        WriteUInt32(data, 8, 1);
        // Bytes +12..+47 are one physically present, zero-valued object record.
        WriteUInt32(data, 48, 1);
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, sizeof(uint)), value);
    }
}
