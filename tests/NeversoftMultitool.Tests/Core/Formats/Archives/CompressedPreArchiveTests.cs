using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CompressedPreArchiveTests(TestPaths paths)
{
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string Thug2WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

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

    /// <summary>
    ///     The plain-v1 PRE parser has no magic and is the fall-through for
    ///     anything IsCompressedPre declines, so an UNKNOWN compressed version
    ///     (a future 0xABCD0004) used to garbage-parse there - its first dword
    ///     is totalFileSize, not an entry count. It must refuse instead.
    ///     Guard added 2026-08-04 (THPS3/THPS4 PS1 corpus bring-up).
    /// </summary>
    [Fact]
    public void PlainPreParser_RefusesUnknownCompressedPreVersions()
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(32).CopyTo(bytes, 0);            // totalFileSize
        BitConverter.GetBytes(0xABCD0004u).CopyTo(bytes, 4);   // unknown version
        BitConverter.GetBytes(1).CopyTo(bytes, 8);             // "numEntries"

        Assert.False(CompressedPreArchive.IsCompressedPre(bytes));
        var exception = Assert.Throws<InvalidDataException>(() => PreArchive.GetFileList(bytes));
        Assert.Contains("0xABCD0004", exception.Message);
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

    [CorpusFact]
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

    [Fact]
    public void LocalizedPre_Prd_ParsesAndExtractsToFullNameDir()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var prdFile = paths.FindSampleFiles(Thug2XboxBuild, "*.prd").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(prdFile == null, "No .prd file found in THUG2 Xbox build");

        Assert.True(CompressedPreArchive.IsCompressedPre(prdFile!));
        var entries = CompressedPreArchive.GetFileList(prdFile!);
        Assert.NotEmpty(entries);

        // Localized variants must extract to a full-name dir (anims.prd/) so they
        // don't merge with the same-stem .pre/.prx siblings.
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Prd_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CompressedPreArchive.ExtractFiles(prdFile!, tempDir, null, TestContext.Current.CancellationToken);

            var expectedDir = Path.Combine(tempDir, Path.GetFileName(prdFile!));
            Assert.True(Directory.Exists(expectedDir), $"Expected extraction dir {Path.GetFileName(prdFile!)}/");
            var extractedPath = Path.Combine(expectedDir, entries[0].FullName);
            Assert.True(File.Exists(extractedPath), $"Extracted file not found: {entries[0].FullName}");
            Assert.Equal(entries[0].Size, new FileInfo(extractedPath).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [CorpusFact]
    public void GetFileList_AllPrdPrfFiles_ParseClean()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = new[] { Thug2XboxBuild, Thug2WindowsBuild }
            .SelectMany(build => paths.FindSampleFiles(build, "*.prd")
                .Concat(paths.FindSampleFiles(build, "*.prf")))
            .ToList();
        Assert.SkipWhen(files.Count == 0, "No .prd/.prf files found");

        var failures = new List<string>();
        var totalEntries = 0;
        foreach (var file in files)
        {
            try
            {
                Assert.True(CompressedPreArchive.IsCompressedPre(file), "not PRE v2/v3");
                var entries = CompressedPreArchive.GetFileList(file);
                Assert.NotEmpty(entries);
                totalEntries += entries.Count;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(316, files.Count);
        Assert.True(totalEntries > 0);
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