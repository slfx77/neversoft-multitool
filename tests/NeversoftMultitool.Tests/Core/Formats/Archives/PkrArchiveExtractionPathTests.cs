using System.IO.Hashing;
using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PkrArchiveExtractionPathTests
{
    [Fact]
    public void ExtractFiles_TraversalEntryFailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pkrPath = Path.Combine(tempRoot, "bundle.pkr");
            WritePkr(pkrPath, "files",
                ("safe.bin", new byte[] { 0x11 }),
                ("../../escaped.bin", new byte[] { 0x22 }));
            var escapedPath = Path.Combine(tempRoot, "escaped.bin");
            File.WriteAllBytes(escapedPath, [0xCC]);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            Assert.Throws<InvalidDataException>(() =>
                PkrArchive.ExtractFiles(pkrPath, output, (_, _) => callbacks++,
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
    public void ExtractFiles_TraversalEmptyDirectoryFailsBeforeCreation()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pkrPath = Path.Combine(tempRoot, "bundle.pkr");
            WritePkr(pkrPath, "../outside");
            var output = Path.Combine(tempRoot, "output");

            Assert.Throws<InvalidDataException>(() =>
                PkrArchive.ExtractFiles(pkrPath, output, token: TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(output));
            Assert.False(Directory.Exists(Path.Combine(tempRoot, "outside")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "nmt-pkr-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WritePkr(string path, string directoryName,
        params (string Name, byte[] Data)[] entries)
    {
        const int directoryOffset = 8;
        const int directoryHeaderSize = 12;
        const int directoryEntrySize = 40;
        const int fileEntrySize = 52;
        var dataOffset = directoryOffset + directoryHeaderSize + directoryEntrySize +
                         fileEntrySize * entries.Length;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("PKR3"u8);
        writer.Write((uint)directoryOffset);
        writer.Write(0u);
        writer.Write(1u);
        writer.Write((uint)entries.Length);
        WriteFixedName(writer, directoryName);
        writer.Write(0u);
        writer.Write((uint)entries.Length);

        uint relativeOffset = 0;
        foreach (var entry in entries)
        {
            WriteFixedName(writer, entry.Name);
            writer.Write(Crc32.HashToUInt32(entry.Data));
            writer.Write(0u);
            writer.Write((uint)(dataOffset + relativeOffset));
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)entry.Data.Length);
            relativeOffset += (uint)entry.Data.Length;
        }

        foreach (var entry in entries)
            writer.Write(entry.Data);
    }

    private static void WriteFixedName(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Assert.InRange(bytes.Length, 0, 32);
        writer.Write(bytes);
        writer.Write(new byte[32 - bytes.Length]);
    }
}
