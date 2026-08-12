using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class DdxArchiveTests
{
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
        const int headerSize = 16;
        const int entrySize = 264;
        var dataOffset = headerSize + entrySize * entries.Length;
        var fileSize = dataOffset + entries.Sum(entry => entry.Data.Length);

        using var stream = File.Create(path);
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
    }
}
