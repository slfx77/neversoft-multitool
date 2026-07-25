using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PkrArchiveTests(TestPaths paths)
{
    private const string SpiderManPcBuild = "Spider-Man (2001-9-17, PC - Final)";

    [Fact]
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

    [Fact]
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

    [Fact]
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
}