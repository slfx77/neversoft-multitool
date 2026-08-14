using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PkrArchiveTests(TestPaths paths)
{
    private const string SpiderManPcBuild = "Spider-Man (2001-9-17, PC - Final)";

    [Fact]
    public void GetFileList_ValidPayloadBeforeDirectoryTable_PreservesHeaderCountTolerance()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [0x11, 0x22, 0x33, 0x44],
            headerFileCount: 99);

        var entry = Assert.Single(PkrArchive.GetFileList(data));

        Assert.Equal("textures", entry.Directory);
        Assert.Equal("sample.dds", entry.Name);
        Assert.Equal(8, entry.Offset);
        Assert.Equal(4, entry.Size);
        Assert.False(entry.IsCompressed);
    }

    [Fact]
    public void GetFileList_TruncatedFileHeaderThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList("PKR3"u8.ToArray()));
    }

    [Fact]
    public void GetFileList_TruncatedDirectoryTableThrowsInvalidData()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            writeDirectoryEntry: false,
            writeFileEntry: false);

        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_HugeDirectoryCountThrowsBeforeAllocation()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            directoryCount: 0x40000000,
            writeDirectoryEntry: false,
            writeFileEntry: false);

        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_TruncatedFileTableThrowsInvalidData()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            writeFileEntry: false);

        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_PayloadOffsetPastEndThrowsInvalidDataEvenWhenEmpty()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            fileOffset: uint.MaxValue,
            storedSize: 0);

        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_PayloadExtentPastEndThrowsInvalidData()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            fileOffset: 112,
            storedSize: 1);

        Assert.Equal(112, data.Length);
        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_PayloadExtentCannotWrapUInt32()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            fileOffset: 100,
            storedSize: uint.MaxValue);

        Assert.True(100 < data.Length);
        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [Fact]
    public void GetFileList_CompressedEntryBoundsStoredSizeRatherThanExpandedSize()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [0x78, 0x9C],
            storedSize: uint.MaxValue,
            compressionFlags: 0x00000002,
            compressedSize: 2);

        var entry = Assert.Single(PkrArchive.GetFileList(data));

        Assert.True(entry.IsCompressed);
        Assert.Equal(uint.MaxValue, entry.Size);
        Assert.Equal(2, entry.CompressedSize);
        Assert.Equal(8, entry.Offset);
    }

    [Fact]
    public void GetFileList_CompressedEntryRejectsStoredExtentPastEnd()
    {
        var data = BuildSingleDirectoryPkr(
            payload: [],
            fileOffset: 111,
            storedSize: 1,
            compressionFlags: 0x00000002,
            compressedSize: 2);

        Assert.Equal(112, data.Length);
        Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(data));
    }

    [CorpusFact]
    public void GetFileList_MatchesGoldenManifest()
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var pkrPath = Path.Combine(paths.PkrDir!, "test.pkr");
        Assert.SkipWhen(!File.Exists(pkrPath), "PKR test file not found");
        Assert.SkipWhen(!File.Exists(paths.GoldenPkrManifest!), "Golden manifest not found");

        var entries = PkrArchive.GetFileList(pkrPath);
        var goldenJson = File.ReadAllText(paths.GoldenPkrManifest!);
        var golden = JsonDocument.Parse(goldenJson);

        var expectedCount = golden.RootElement.GetProperty("fileCount").GetInt32();
        Assert.Equal(expectedCount, entries.Count);

        var goldenEntries = golden.RootElement.GetProperty("entries");
        for (var i = 0; i < entries.Count; i++)
        {
            var expected = goldenEntries[i];
            Assert.Equal(expected.GetProperty("name").GetString(), entries[i].Name);
            Assert.Equal(expected.GetProperty("directory").GetString(), entries[i].Directory);
            Assert.Equal(expected.GetProperty("size").GetUInt32(), entries[i].Size);
            Assert.Equal(expected.GetProperty("crc").GetUInt32(), entries[i].Crc);
            Assert.Equal(expected.GetProperty("isCompressed").GetBoolean(), entries[i].IsCompressed);
        }
    }

    [CorpusFact]
    public void ExtractFiles_AllFilesExtracted_WithCorrectContent()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var pkrPath = Path.Combine(paths.PkrDir!, "test.pkr");
        Assert.SkipWhen(!File.Exists(pkrPath), "PKR test file not found");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Pkr_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            var extractedCount = 0;
            PkrArchive.ExtractFiles(pkrPath, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            Assert.Equal(4, extractedCount);

            // Verify hello.txt content
            var helloPath = Path.Combine(tempDir, "testdir", "hello.txt");
            Assert.True(File.Exists(helloPath));
            Assert.Equal("Hello World from PKR!\n", File.ReadAllText(helloPath));

            // Verify data.bin (was compressed) - should be 256 bytes repeated 4 times
            var dataPath = Path.Combine(tempDir, "testdir", "data.bin");
            Assert.True(File.Exists(dataPath));
            var dataBytes = File.ReadAllBytes(dataPath);
            Assert.Equal(1024, dataBytes.Length);
            for (var i = 0; i < 1024; i++)
            {
                Assert.Equal((byte)(i % 256), dataBytes[i]);
            }

            // Verify small.dat content
            var smallPath = Path.Combine(tempDir, "subdir", "small.dat");
            Assert.True(File.Exists(smallPath));
            var smallBytes = File.ReadAllBytes(smallPath);
            Assert.Equal(32, smallBytes.Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_InvalidMagic_ThrowsInvalidData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "invalid.pkr");
        try
        {
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            Assert.Throws<InvalidDataException>(() => PkrArchive.GetFileList(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [CorpusFact]
    public void SpiderManPcDataPkr_NormalizesDirectorySeparatorsInDisplayPaths()
    {
        var pkrPath = paths.FindSampleFile(SpiderManPcBuild, "data.pkr");
        Assert.SkipWhen(pkrPath == null, "Spider-Man PC data.pkr sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pkrPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("jameson.psx");
        Assert.NotNull(entry);

        Assert.Equal("data", entry!.Directory);
        Assert.Equal("data/jameson.PSX", entry.FullName);
        Assert.Equal(
            "data.pkr::data/jameson.PSX",
            new ArchiveAssetSource(backend, entry).DisplayName);
    }

    private static byte[] BuildSingleDirectoryPkr(
        byte[] payload,
        uint directoryCount = 1,
        uint headerFileCount = 1,
        uint directoryFileCount = 1,
        uint? fileOffset = null,
        uint? storedSize = null,
        uint compressionFlags = 0,
        uint compressedSize = 0,
        bool writeDirectoryEntry = true,
        bool writeFileEntry = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);

        writer.Write("PKR3"u8);
        writer.Write((uint)(8 + payload.Length));
        writer.Write(payload);

        writer.Write(0u); // directory-header unknown
        writer.Write(directoryCount);
        writer.Write(headerFileCount); // advisory; directory records own the walk

        if (writeDirectoryEntry)
        {
            WriteFixedName(writer, "textures");
            writer.Write(0u); // directory unknown
            writer.Write(directoryFileCount);
        }

        if (writeFileEntry)
        {
            WriteFixedName(writer, "sample.dds");
            writer.Write(0u); // CRC (listing does not validate payload contents)
            writer.Write(compressionFlags);
            writer.Write(fileOffset ?? 8u);
            writer.Write(storedSize ?? (uint)payload.Length);
            writer.Write(compressedSize);
        }

        return stream.ToArray();
    }

    private static void WriteFixedName(BinaryWriter writer, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        Assert.InRange(bytes.Length, 0, 32);
        writer.Write(bytes);
        writer.Write(new byte[32 - bytes.Length]);
    }
}
