using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public sealed class PakArchiveExtractionIntegrityTests
{
    private const uint LastSentinel = 0xB524565F;
    private const uint FileType = 0xA7F505C4;
    private const int FullEntrySize = 0xC0;
    private const int SentinelSize = 0x20;

    [Fact]
    public void ExtractFiles_TruncatedLaterEntry_FailsBeforeAnyOutputOrCallback()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.pak");
            WriteLittleEndianPak(
                pakPath,
                ("safe/first.bin", new byte[] { 0x11 }),
                ("safe/second.bin", new byte[] { 0x22 }));
            using (var stream = File.Open(pakPath, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(stream.Length - 1);

            Assert.Equal(2, PakArchive.GetFileList(pakPath).Count);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            var exception = Assert.Throws<InvalidDataException>(() =>
                PakArchive.ExtractFiles(
                    pakPath,
                    output,
                    (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Contains("safe/second.bin", exception.Message);
            Assert.Contains("data range", exception.Message);
            Assert.Equal(0, callbacks);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractFiles_CompanionResidentEntryWithoutRealCompanion_FailsBeforeOutput(
        bool createPlaceholder)
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.apk.ngc");
            WriteBigEndianCompanionPak(pakPath, "safe/companion.bin", payloadSize: 4);
            if (createPlaceholder)
                File.WriteAllBytes(PakArchive.GetPabPath(pakPath), new byte[SentinelSize]);
            var entry = Assert.Single(PakArchive.GetFileList(pakPath));
            Assert.True(entry.InCompanion);
            Assert.Equal(0, entry.Offset);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            var exception = Assert.Throws<InvalidDataException>(() =>
                PakArchive.ExtractFiles(
                    pakPath,
                    output,
                    (_, _) => callbacks++,
                    TestContext.Current.CancellationToken));

            Assert.Contains("companion.bin", exception.Message);
            Assert.Contains("PAK/companion data", exception.Message);
            Assert.Equal(0, callbacks);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExtractFiles_SentinelOnlyArchive_RemainsANoOp()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "empty.pak");
            using (var writer = new BinaryWriter(File.Create(pakPath)))
            {
                writer.Write(LastSentinel);
                writer.Write(new byte[SentinelSize - sizeof(uint)]);
            }

            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;
            PakArchive.ExtractFiles(
                pakPath,
                output,
                (_, _) => callbacks++,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, callbacks);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExtractFiles_CompanionResidentEntryWithCompanion_ExtractsPayload()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var pakPath = Path.Combine(tempRoot, "bundle.apk.ngc");
            WriteBigEndianCompanionPak(pakPath, "safe/companion.bin", payloadSize: 4);
            var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            var companion = new byte[36];
            payload.CopyTo(companion, 0);
            File.WriteAllBytes(PakArchive.GetPabPath(pakPath), companion);
            var output = Path.Combine(tempRoot, "output");
            var callbacks = 0;

            PakArchive.ExtractFiles(
                pakPath,
                output,
                (_, _) => callbacks++,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, callbacks);
            Assert.Equal(
                payload,
                File.ReadAllBytes(Path.Combine(output, "bundle.apk", "safe", "companion.bin")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "NsMtPakIntegrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteLittleEndianPak(
        string path,
        params (string Name, byte[] Data)[] entries)
    {
        var sentinelOffset = FullEntrySize * entries.Length;
        var payloadOffset = sentinelOffset + SentinelSize;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        for (var i = 0; i < entries.Length; i++)
        {
            var entryOffset = FullEntrySize * i;
            WriteEntry(
                writer,
                entries[i].Name,
                checked((uint)(payloadOffset - entryOffset)),
                checked((uint)entries[i].Data.Length),
                bigEndian: false);
            payloadOffset += entries[i].Data.Length;
        }

        writer.Write(LastSentinel);
        writer.Write(new byte[SentinelSize - sizeof(uint)]);
        foreach (var entry in entries)
            writer.Write(entry.Data);
    }

    private static void WriteBigEndianCompanionPak(string path, string name, uint payloadSize)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        WriteEntry(writer, name, offset: 0, payloadSize, bigEndian: true);
        WriteU32(writer, LastSentinel, bigEndian: true);
        writer.Write(new byte[SentinelSize - sizeof(uint)]);
    }

    private static void WriteEntry(
        BinaryWriter writer,
        string name,
        uint offset,
        uint payloadSize,
        bool bigEndian)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Assert.InRange(nameBytes.Length, 1, 159);

        WriteU32(writer, FileType, bigEndian);
        WriteU32(writer, offset, bigEndian);
        WriteU32(writer, payloadSize, bigEndian);
        writer.Write(new byte[16]);
        WriteU32(writer, 0x20, bigEndian);
        writer.Write(nameBytes);
        writer.Write(new byte[160 - nameBytes.Length]);
    }

    private static void WriteU32(BinaryWriter writer, uint value, bool bigEndian)
    {
        if (!bigEndian)
        {
            writer.Write(value);
            return;
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }
}
