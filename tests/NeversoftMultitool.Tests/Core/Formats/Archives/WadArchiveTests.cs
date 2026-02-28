using System.Text.Json;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class WadArchiveTests(TestPaths paths)
{
    [Fact]
    public void GetFileList_MatchesGoldenManifest()
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var wadPath = Path.Combine(paths.WadDir!, "CD.WAD");
        Assert.SkipWhen(!File.Exists(wadPath), "WAD test file not found");
        Assert.SkipWhen(!File.Exists(paths.GoldenWadManifest!), "Golden manifest not found");

        var entries = WadArchive.GetFileList(wadPath);
        var goldenJson = File.ReadAllText(paths.GoldenWadManifest!);
        var golden = JsonDocument.Parse(goldenJson);

        var expectedCount = golden.RootElement.GetProperty("fileCount").GetInt32();
        Assert.Equal(expectedCount, entries.Count);

        var goldenEntries = golden.RootElement.GetProperty("entries");
        for (var i = 0; i < entries.Count; i++)
        {
            var expected = goldenEntries[i];
            Assert.Equal(expected.GetProperty("name").GetString(), entries[i].Name);
            Assert.Equal(expected.GetProperty("size").GetUInt32(), entries[i].Size);
            Assert.Equal(expected.GetProperty("offset").GetUInt32(), (uint)entries[i].Offset);
        }
    }

    [Fact]
    public void ExtractFiles_AllFilesExtracted()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var wadPath = Path.Combine(paths.WadDir!, "CD.WAD");
        Assert.SkipWhen(!File.Exists(wadPath), "WAD test file not found");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Wad_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var expectedEntries = WadArchive.GetFileList(wadPath);

            var extractedCount = 0;
            WadArchive.ExtractFiles(wadPath, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedEntries.Count, extractedCount);

            // Verify each extracted file exists and has correct size
            foreach (var entry in expectedEntries)
            {
                var extractedPath = Path.Combine(tempDir, "CD", entry.Name);
                Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entry.Name}");
                var info = new FileInfo(extractedPath);
                Assert.Equal(entry.Size, (uint)info.Length);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_MissingHed_ThrowsFileNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_NoHed");
        var tempWad = Path.Combine(tempDir, "missing.WAD");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempWad, [0x00]);

            Assert.Throws<FileNotFoundException>(() => WadArchive.GetFileList(tempWad));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}