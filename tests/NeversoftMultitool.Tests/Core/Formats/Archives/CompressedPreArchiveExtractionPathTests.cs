using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CompressedPreArchiveExtractionPathTests
{
    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var prePath = Path.Combine(tempRoot, "level.pre");
            WriteVersion2Pre(prePath,
                ("safe.bin", new byte[] { 0x11 }),
                ("../../escaped.bin", new byte[] { 0x22 }));
            var escapedPath = Path.Combine(tempRoot, "escaped.bin");
            File.WriteAllBytes(escapedPath, [0xCC]);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                CompressedPreArchive.ExtractFiles(prePath, output, (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, callbacks);
            Assert.Equal(new byte[] { 0xCC }, File.ReadAllBytes(escapedPath));
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
            WriteVersion2Pre(prePath, ("safe.bin", new byte[] { 0x11 }));
            Assert.Equal("..", ArchiveNaming.GetExtractionStem(prePath));
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                CompressedPreArchive.ExtractFiles(
                    prePath, output, token: TestContext.Current.CancellationToken));

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
        var path = Path.Combine(Path.GetTempPath(), "nmt-compressed-pre-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteVersion2Pre(string path, params (string Name, byte[] Data)[] entries)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(0);
        writer.Write(0xABCD0002u);
        writer.Write(entries.Length);

        foreach (var entry in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(entry.Name + "\0");
            var nameFieldSize = (nameBytes.Length + 3) & ~3;
            Assert.InRange(nameFieldSize, 1, short.MaxValue);
            writer.Write(entry.Data.Length);
            writer.Write(0);
            writer.Write((short)nameFieldSize);
            writer.Write((short)0);
            writer.Write(nameBytes);
            writer.Write(new byte[nameFieldSize - nameBytes.Length]);
            writer.Write(entry.Data);
            var padding = (4 - entry.Data.Length % 4) % 4;
            for (var i = 0; i < padding; i++)
                writer.Write((byte)0);
        }

        writer.Flush();
        var totalFileSize = checked((int)stream.Length);
        stream.Position = 0;
        writer.Write(totalFileSize);
    }
}
