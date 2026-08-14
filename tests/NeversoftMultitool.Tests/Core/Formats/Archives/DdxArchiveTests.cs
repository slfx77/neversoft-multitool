using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class DdxArchiveTests
{
    [Fact]
    public void ReadAllEntries_ValidSyntheticArchiveReturnsDeclaredPayloads()
    {
        var data = BuildDdx(
            ("first.dds", "first"u8.ToArray()),
            ("second.dds", "second"u8.ToArray()));

        var entries = DdxArchive.GetFileList(data);
        var payloads = DdxArchive.ReadAllEntries(data);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("first.dds", entry.Name);
                Assert.Equal(5, entry.Size);
                Assert.Equal(544, entry.Offset);
            },
            entry =>
            {
                Assert.Equal("second.dds", entry.Name);
                Assert.Equal(6, entry.Size);
                Assert.Equal(549, entry.Offset);
            });
        Assert.Equal("first"u8.ToArray(), payloads["first"]);
        Assert.Equal("second"u8.ToArray(), payloads["second"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void GetFileList_TruncatedHeaderThrowsInvalidData(int length)
    {
        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(new byte[length]));
    }

    [Fact]
    public void GetFileList_TruncatedTableThrowsInvalidData()
    {
        var data = BuildRawDdx(279, dataOffset: 280, entryCount: 1);

        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_UIntMaxEntryCountThrowsInvalidDataBeforeAllocation()
    {
        var data = BuildRawDdx(16, dataOffset: 16, entryCount: uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_DataRegionInsideTableThrowsInvalidData()
    {
        var data = BuildRawDdx(280, dataOffset: 16, entryCount: 1);

        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_OffsetAdditionCannotWrapIntoTable()
    {
        var data = BuildRawDdx(281, dataOffset: 280, entryCount: 1,
            relativeOffset: uint.MaxValue, size: 1);

        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_PayloadPastEndThrowsInvalidData()
    {
        var data = BuildRawDdx(281, dataOffset: 280, entryCount: 1,
            relativeOffset: 0, size: 2);

        Assert.Throws<InvalidDataException>(() => DdxArchive.GetFileList(data));
    }

    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var ddxPath = Path.Combine(tempRoot, "textures.ddx");
            WriteDdx(ddxPath,
                ("safe.dds", "safe"u8.ToArray()),
                ("../../escaped.dds", "evil"u8.ToArray()));
            var escapedPath = Path.Combine(tempRoot, "escaped.dds");
            File.WriteAllBytes(escapedPath, "original"u8.ToArray());
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                DdxArchive.ExtractFiles(ddxPath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Equal("original"u8.ToArray(), File.ReadAllBytes(escapedPath));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ExtractFiles_TraversalArchiveStemFailsBeforeAnyOutput()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var ddxPath = Path.Combine(tempRoot, "...ddx");
            WriteDdx(ddxPath, ("safe.dds", "safe"u8.ToArray()));
            Assert.Equal("..", Path.GetFileNameWithoutExtension(ddxPath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                DdxArchive.ExtractFiles(ddxPath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(tempRoot, "safe.dds")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-ddx-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteDdx(string path, params (string Name, byte[] Data)[] entries)
    {
        File.WriteAllBytes(path, BuildDdx(entries));
    }

    private static byte[] BuildDdx(params (string Name, byte[] Data)[] entries)
    {
        const int headerSize = 16;
        const int entrySize = 264;
        var dataOffset = headerSize + entrySize * entries.Length;
        var fileSize = dataOffset + entries.Sum(entry => entry.Data.Length);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(0u);
        writer.Write((uint)fileSize);
        writer.Write((uint)dataOffset);
        writer.Write((uint)entries.Length);

        uint relativeOffset = 0;
        foreach (var entry in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
            Assert.InRange(nameBytes.Length, 1, 256);
            writer.Write(relativeOffset);
            writer.Write((uint)entry.Data.Length);
            writer.Write(nameBytes);
            writer.Write(new byte[256 - nameBytes.Length]);
            relativeOffset += (uint)entry.Data.Length;
        }

        foreach (var entry in entries)
            writer.Write(entry.Data);

        return stream.ToArray();
    }

    private static byte[] BuildRawDdx(int length, uint dataOffset, uint entryCount,
        uint relativeOffset = 0, uint size = 0)
    {
        Assert.True(length >= 16);
        var data = new byte[length];
        using var stream = new MemoryStream(data);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(0u);
        writer.Write((uint)length);
        writer.Write(dataOffset);
        writer.Write(entryCount);
        if (length >= 24)
        {
            writer.Write(relativeOffset);
            writer.Write(size);
        }

        return data;
    }
}
