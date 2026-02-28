using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CompressedPreArchiveTests(TestPaths paths)
{
    [Fact]
    public void IsCompressedPre_WithPs1PreFile_ReturnsFalse()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Find a PS1 PRE file (these are the old uncompressed format)
        var preDir = FindBuildSubdir("Archives", "PRE");
        Assert.SkipWhen(preDir == null, "No PRE archives found in sample builds");

        var preFile = Directory.EnumerateFiles(preDir, "*.pre", SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(preFile == null, "No .pre file found");

        Assert.False(CompressedPreArchive.IsCompressedPre(preFile!));
    }

    [Fact]
    public void IsCompressedPre_WithV3PreFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindPs2PreFile();
        Assert.SkipWhen(preFile == null, "No PS2 PRE/PRX files found in sample builds");

        Assert.True(CompressedPreArchive.IsCompressedPre(preFile!));
    }

    [Fact]
    public void GetFileList_Ps2Pre_ReturnsNonEmptyList()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindPs2PreFile();
        Assert.SkipWhen(preFile == null, "No PS2 PRE/PRX files found in sample builds");

        var entries = CompressedPreArchive.GetFileList(preFile!);
        Assert.NotEmpty(entries);

        // All entries should have non-empty names and positive sizes
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), "Entry has empty name");
            Assert.True(entry.Size > 0, $"Entry '{entry.FullName}' has zero size");
        }
    }

    [Fact]
    public void ExtractFiles_Ps2Pre_AllFilesExtracted()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFile = FindPs2PreFile();
        Assert.SkipWhen(preFile == null, "No PS2 PRE/PRX files found in sample builds");

        var entries = CompressedPreArchive.GetFileList(preFile!);
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PreV3_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var extractedCount = 0;
            CompressedPreArchive.ExtractFiles(preFile!, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            Assert.Equal(entries.Count, extractedCount);

            // Verify each extracted file exists and has correct decompressed size
            var archiveName = Path.GetFileNameWithoutExtension(preFile!);
            foreach (var entry in entries)
            {
                var extractedPath = Path.Combine(tempDir, archiveName, entry.FullName);
                Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entry.FullName}");

                var info = new FileInfo(extractedPath);
                Assert.Equal(entry.Size, info.Length);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_InvalidVersion_ThrowsInvalidData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_invalid.pre");
        try
        {
            // Write a file with wrong version
            using (var stream = File.Create(tempFile))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(100); // totalFileSize
                writer.Write(0x12345678); // wrong version
                writer.Write(0); // numEntries
            }

            Assert.Throws<InvalidDataException>(() => CompressedPreArchive.GetFileList(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetFileList_AllPs2PreFiles_ParseWithoutErrors()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var preFiles = FindAllPs2PreFiles();
        Assert.SkipWhen(preFiles.Count == 0, "No PS2 PRE/PRX files found");

        var totalEntries = 0;
        foreach (var preFile in preFiles)
        {
            var entries = CompressedPreArchive.GetFileList(preFile);
            Assert.NotNull(entries);
            totalEntries += entries.Count;
        }

        Assert.True(totalEntries > 0, "No entries found across all PS2 PRE files");
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private string? FindBuildSubdir(params string[] parts)
    {
        if (paths.SampleBuildsDir == null) return null;

        foreach (var buildDir in Directory.EnumerateDirectories(paths.SampleBuildsDir))
        {
            var candidate = Path.Combine([buildDir, .. parts]);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private string? FindPs2PreFile()
    {
        var files = FindAllPs2PreFiles();
        return files.Count > 0 ? files[0] : null;
    }

    private List<string> FindAllPs2PreFiles()
    {
        if (paths.SampleBuildsDir == null) return [];

        var result = new List<string>();
        foreach (var buildDir in Directory.EnumerateDirectories(paths.SampleBuildsDir))
        {
            // Only look in PS2 builds
            if (!buildDir.Contains("PS2", StringComparison.OrdinalIgnoreCase)) continue;

            var preDir = Path.Combine(buildDir, "Archives", "PRE");
            if (!Directory.Exists(preDir)) continue;

            foreach (var file in Directory.EnumerateFiles(preDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".pre", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".prx", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CompressedPreArchive.IsCompressedPre(file))
                    result.Add(file);
            }
        }

        return result;
    }
}