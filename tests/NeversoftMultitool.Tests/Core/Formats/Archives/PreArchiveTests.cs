using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PreArchiveTests
{
    [Fact]
    public void GetFileList_ValidBytes_ReturnsFramedEntries()
    {
        var data = BuildPlainPre(
            ("a.bin", new byte[] { 0x11, 0x22, 0x33 }),
            ("long-name.dat", new byte[] { 0x44 }));

        var entries = PreArchive.GetFileList(data);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("a.bin", entry.Name);
                Assert.Equal(3u, entry.Size);
                Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, data[(int)entry.Offset..(int)(entry.Offset + entry.Size)]);
            },
            entry =>
            {
                Assert.Equal("long-name.dat", entry.Name);
                Assert.Equal(1u, entry.Size);
                Assert.Equal(new byte[] { 0x44 }, data[(int)entry.Offset..(int)(entry.Offset + entry.Size)]);
            });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GetFileList_ShortHeader_ThrowsInvalidDataException(int length)
    {
        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(new byte[length]));
    }

    [Fact]
    public void GetFileList_ImpossibleEntryCount_ThrowsInvalidDataException()
    {
        var data = new byte[12];
        BitConverter.TryWriteBytes(data, 2u);

        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_HugeEntryCount_ThrowsInvalidDataException()
    {
        var data = BitConverter.GetBytes(uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_UnterminatedName_ThrowsInvalidDataException()
    {
        var data = new byte[12];
        BitConverter.TryWriteBytes(data, 1u);
        Array.Fill(data, (byte)'A', 4, 8);

        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_TruncatedSizeField_ThrowsInvalidDataException()
    {
        var data = new byte[12];
        BitConverter.TryWriteBytes(data, 1u);
        data[4] = (byte)'n';
        data[5] = (byte)'a';
        data[6] = (byte)'m';
        data[7] = (byte)'e';
        data[8] = 0;

        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_DeclaredPayloadPastEnd_ThrowsInvalidDataException()
    {
        var data = new byte[15];
        BitConverter.TryWriteBytes(data, 1u);
        data[4] = 0;
        BitConverter.TryWriteBytes(data.AsSpan(8), 4u);

        Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(data));
    }

    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var prePath = Path.Combine(tempRoot, "level.pre");
            WritePlainPre(prePath,
                ("safe.bin", new byte[] { 0x11 }),
                ("../../escape.bin", new byte[] { 0x22 }));
            var escapePath = Path.Combine(tempRoot, "escape.bin");
            File.WriteAllBytes(escapePath, [0xCC]);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                PreArchive.ExtractFiles(prePath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Equal(new byte[] { 0xCC }, File.ReadAllBytes(escapePath));
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
            var prePath = Path.Combine(tempRoot, "...pre");
            WritePlainPre(prePath, ("safe.bin", new byte[] { 0x11 }));
            Assert.Equal("..", ArchiveNaming.GetExtractionStem(prePath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                PreArchive.ExtractFiles(prePath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(tempRoot, "safe.bin")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-pre-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WritePlainPre(string path, params (string Name, byte[] Data)[] entries)
    {
        using var stream = File.Create(path);
        WritePlainPre(stream, entries);
    }

    private static byte[] BuildPlainPre(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        WritePlainPre(stream, entries);
        return stream.ToArray();
    }

    private static void WritePlainPre(Stream stream, params (string Name, byte[] Data)[] entries)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write((uint)entries.Length);
        foreach (var entry in entries)
        {
            AlignTo4(writer);
            writer.Write(Encoding.ASCII.GetBytes(entry.Name));
            writer.Write((byte)0);
            AlignTo4(writer);
            writer.Write((uint)entry.Data.Length);
            writer.Write(entry.Data);
        }
    }

    private static void AlignTo4(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0)
            writer.Write((byte)0);
    }
}
