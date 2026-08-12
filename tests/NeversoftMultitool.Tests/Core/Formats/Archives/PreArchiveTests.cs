using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PreArchiveTests
{
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
