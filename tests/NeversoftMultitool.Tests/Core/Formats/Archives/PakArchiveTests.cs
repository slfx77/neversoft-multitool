using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PakArchiveTests(TestPaths paths)
{
    private const string ThawPakDir =
        "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)/PAK";

    private string? GetPakDir()
    {
        if (!paths.HasSampleBuilds) return null;
        var dir = Path.Combine(paths.SampleBuildsDir!, ThawPakDir);
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void IsPakArchive_WithArchivePak_ReturnsTrue()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "qb.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "qb.pak.ps2 not found");

        Assert.True(PakArchive.IsPakArchive(pakPath));
    }

    [Fact]
    public void IsPakArchive_WithShellPak_ReturnsTrue()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "cap_shell2.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "cap_shell2.pak.ps2 not found");

        Assert.True(PakArchive.IsPakArchive(pakPath));
    }

    [Fact]
    public void IsPakArchive_WithRawDataPak_ReturnsFalse()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "cap_assets_fast_particle_data.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "cap_assets_fast_particle_data.pak.ps2 not found");

        Assert.False(PakArchive.IsPakArchive(pakPath));
    }

    [Fact]
    public void GetFileList_QbPak_Returns241Entries()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "qb.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "qb.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath);
        Assert.Equal(241, entries.Count);
        Assert.All(entries, e => Assert.True(e.Size > 0, $"Entry {e.Name} has zero size"));

        // At least one named entry should exist
        var namedEntries = entries.Where(e => e.Directory.Length > 0).ToList();
        Assert.True(namedEntries.Count > 0, "Expected at least one named entry");
    }

    [Fact]
    public void GetFileList_CapShellPak_ReturnsShellEntries()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "cap_shell1.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "cap_shell1.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath);
        Assert.Equal(5, entries.Count);

        Assert.Contains(entries, e =>
            e.FullName.Equals("worlds/createapark/cap_shell1/cap_shell1.qb.ps2",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".col", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFiles_QbPak_AllFilesExtracted()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "qb.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "qb.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Pak_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            var extractedCount = 0;
            var entries = PakArchive.GetFileList(pakPath);
            PakArchive.ExtractFiles(pakPath, tempDir, (current, total) => { extractedCount = current; },
                TestContext.Current.CancellationToken);

            var extractedFiles = Directory.GetFiles(
                Path.Combine(tempDir, "qb.pak"), "*", SearchOption.AllDirectories);
            Assert.True(extractedFiles.Length >= 200,
                $"Expected at least 200 extracted files on disk, got {extractedFiles.Length}");
            Assert.True(extractedFiles.Length <= extractedCount,
                $"Expected extracted file count to not exceed progress count ({extractedCount}), got {extractedFiles.Length}");

            // Verify non-zero file sizes
            Assert.All(extractedFiles, f =>
                Assert.True(new FileInfo(f).Length > 0, $"Extracted file is empty: {f}"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetPabPath_DoubleExtension_ReturnsCorrectPath()
    {
        Assert.Equal(
            Path.Combine("dir", "qb.pab.ps2"),
            PakArchive.GetPabPath(Path.Combine("dir", "qb.pak.ps2")));

        Assert.Equal(
            Path.Combine("dir", "global.pab.xen"),
            PakArchive.GetPabPath(Path.Combine("dir", "global.pak.xen")));
    }

    [Fact]
    public void Parse_AllPakFiles_NoExceptions()
    {
        var pakDir = GetPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakFiles = Directory.GetFiles(pakDir!, "*.pak.ps2");
        Assert.SkipWhen(pakFiles.Length == 0, "No PAK files found");

        var archiveCount = 0;
        var rawCount = 0;
        var totalEntries = 0;
        var errors = new List<string>();

        foreach (var pakFile in pakFiles)
        {
            try
            {
                if (PakArchive.IsPakArchive(pakFile))
                {
                    archiveCount++;
                    var entries = PakArchive.GetFileList(pakFile);
                    totalEntries += entries.Count;
                }
                else
                {
                    rawCount++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(pakFile)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"{errors.Count} parse errors:\n{string.Join("\n", errors.Take(10))}");

        // Validate broad corpus coverage without pinning the exact split between table-backed and raw PAKs.
        Assert.True(archiveCount >= 700, $"Expected ≥700 archives, got {archiveCount}");
        Assert.True(rawCount >= 50, $"Expected ≥50 raw data files, got {rawCount}");
        Assert.True(totalEntries >= 30000, $"Expected ≥30,000 entries, got {totalEntries}");
    }
}