using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class PakArchiveTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [Fact]
    public void IsPakArchive_WithArchivePak_ReturnsTrue()
    {
        var pakPath = paths.FindSampleFile(BuildName, "qb.pak.ps2");
        Assert.SkipWhen(pakPath is null, "qb.pak.ps2 not found");

        Assert.True(PakArchive.IsPakArchive(pakPath!));
    }

    [Fact]
    public void IsPakArchive_WithShellPak_ReturnsTrue()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_shell2.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_shell2.pak.ps2 not found");

        Assert.True(PakArchive.IsPakArchive(pakPath!));
    }

    [Fact]
    public void IsPakArchive_WithSkyPak_ReturnsTrue()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_shell1_sky.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_shell1_sky.pak.ps2 not found");

        Assert.True(PakArchive.IsPakArchive(pakPath!));
    }

    [Fact]
    public void IsPakArchive_WithRawDataPak_ReturnsFalse()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_assets_fast_particle_data.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_assets_fast_particle_data.pak.ps2 not found");

        Assert.False(PakArchive.IsPakArchive(pakPath!));
    }

    [Fact]
    public void GetFileList_QbPak_Returns266Entries()
    {
        var pakPath = paths.FindSampleFile(BuildName, "qb.pak.ps2");
        Assert.SkipWhen(pakPath is null, "qb.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(266, entries.Count);
        Assert.All(entries, e => Assert.True(e.Size > 0, $"Entry {e.Name} has zero size"));

        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/zone_sizes_ps2.qb.ps2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/game/game.qb.ps2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/plugin/plugin.qb.ps2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFileList_CapShellPak_ReturnsShellEntries()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_shell1.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_shell1.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(5, entries.Count);

        Assert.Contains(entries, e =>
            e.FullName.Equals("worlds/createapark/cap_shell1/cap_shell1.qb.ps2",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".col", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFileList_CapShellSkyPak_ReturnsSkyEntries()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_shell1_sky.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_shell1_sky.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(4, entries.Count);

        Assert.Contains(entries, e =>
            e.FullName.Equals("skies/cap_shell1_sky/cap_shell1_sky.qb.ps2",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFileList_CapShellPak_UsesNormalOffsetSizeOrder()
    {
        var pakPath = paths.FindSampleFile(BuildName, "cap_shell1.pak.ps2");
        Assert.SkipWhen(pakPath is null, "cap_shell1.pak.ps2 not found");

        var entries = PakArchive.GetFileList(pakPath!);

        var texEntry = Assert.Single(entries, e => e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x000009B0u, texEntry.Offset);
        Assert.Equal(0x00041BF0u, texEntry.Size);

        var mdlEntry = Assert.Single(entries, e => e.Name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x000425A0u, mdlEntry.Offset);
        Assert.Equal(0x0007DF80u, mdlEntry.Size);

        var colEntry = Assert.Single(entries, e => e.Name.EndsWith(".col", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x000C0520u, colEntry.Offset);
        Assert.Equal(0x0002901Au, colEntry.Size);
    }

    [Fact]
    public void ExtractFiles_QbPak_AllFilesExtracted()
    {
        var pakPath = paths.FindSampleFile(BuildName, "qb.pak.ps2");
        Assert.SkipWhen(pakPath is null, "qb.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Pak_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            var extractedCount = 0;
            PakArchive.ExtractFiles(pakPath!, tempDir, (current, total) => { extractedCount = current; },
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

    [CorpusFact]
    public void Parse_AllPakFiles_NoExceptions()
    {
        var pakFiles = paths.FindSampleFiles(BuildName, "*.pak.ps2").ToArray();
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

/// <summary>
///     THAW GameCube big-endian PAK variant (.apk.ngc / .pak.ngc with .mpk.ngc companions).
/// </summary>
public class NgcPakArchiveTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    [Fact]
    public void IsPakArchive_ApkNgc_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "cagr_assets_f.apk.ngc");
        Assert.SkipWhen(file is null, "cagr_assets_f.apk.ngc not found");

        Assert.True(PakArchive.IsPakArchive(file));
    }

    [Fact]
    public void IsPakArchive_SfxRawPak_ReturnsFalse()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Z_BH_sfx.pak.ngc");
        Assert.SkipWhen(file is null, "Z_BH_sfx.pak.ngc not found");

        Assert.False(PakArchive.IsPakArchive(file));
    }

    [Fact]
    public void GetFileList_CagrAssets_TilesInPakOffsets()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "cagr_assets_f.apk.ngc");
        Assert.SkipWhen(file is null, "cagr_assets_f.apk.ngc not found");

        var entries = PakArchive.GetFileList(file);
        Assert.Equal(723, entries.Count);

        // In-pak offsets resolve to the physical tiled layout: the stated offsets
        // describe the loader's header-hoisted view (entry 1 states 0x5F20).
        Assert.Equal(0x5B20, entries[0].Offset);
        Assert.Equal(0x5F40, entries[1].Offset);
        Assert.False(entries[0].InCompanion);

        // Every resolved .img block starts with a bare NGC texture record.
        var data = File.ReadAllBytes(file);
        foreach (var entry in entries.Where(e => e.Name.EndsWith(".img.ngc", StringComparison.Ordinal)))
        {
            Assert.True(data[entry.Offset] == 0x04 && data[entry.Offset + 1] == 0x20,
                $"{entry.Name}: no texture record at resolved offset 0x{entry.Offset:X}");
        }
    }

    [Fact]
    public void GetFileList_CutsceneMain_MarksCompanionEntries()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "bh_11_main.apk.ngc");
        Assert.SkipWhen(file is null, "bh_11_main.apk.ngc not found");

        var entries = PakArchive.GetFileList(file);
        Assert.Equal(67, entries.Count);
        Assert.Equal(63, entries.Count(e => e.InCompanion));
    }

    [Fact]
    public void GetFileList_QbPakNgc_ParsesEmbeddedFilenames()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "qb_i.pak.ngc");
        Assert.SkipWhen(file is null, "qb_i.pak.ngc not found");

        var entries = PakArchive.GetFileList(file);
        Assert.Equal(269, entries.Count);
        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/zone_sizes_ngc.qb.ngc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPabPath_ApkNgc_ReturnsMpkCompanion()
    {
        Assert.Equal(
            Path.Combine("dir", "bh_11_main.mpk.ngc"),
            PakArchive.GetPabPath(Path.Combine("dir", "bh_11_main.apk.ngc")));
    }

    [Fact]
    public void ExtractFiles_CutsceneMain_ResolvesCompanionData()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "bh_11_main.apk.ngc");
        Assert.SkipWhen(file is null, "bh_11_main.apk.ngc not found");
        Assert.SkipWhen(!File.Exists(PakArchive.GetPabPath(file!)), "companion .mpk.ngc not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_NgcPak_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(file!, tempDir, token: TestContext.Current.CancellationToken);

            var extracted = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            Assert.True(extracted.Length >= 60,
                $"Expected ≥60 extracted files, got {extracted.Length}");
            Assert.All(extracted, f =>
                Assert.True(new FileInfo(f).Length > 0, $"Extracted file is empty: {f}"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [CorpusFact]
    public void BatchParse_AllGcPaks_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.apk.ngc")
            .Concat(paths.FindSampleFiles(BuildName, "*.pak.ngc"))
            .ToArray();
        Assert.SkipWhen(files.Length == 0, "No GC PAK files found");

        var archives = 0;
        var raw = 0;
        var totalEntries = 0;
        var errors = new List<string>();

        foreach (var f in files)
        {
            try
            {
                if (PakArchive.IsPakArchive(f))
                {
                    archives++;
                    totalEntries += PakArchive.GetFileList(f).Count;
                }
                else
                {
                    raw++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"{errors.Count} parse errors:\n{string.Join("\n", errors.Take(10))}");
        Assert.True(archives >= 4400, $"Expected ≥4,400 archives, got {archives}");
        Assert.True(raw >= 40, $"Expected ≥40 raw sfx paks, got {raw}");
        Assert.True(totalEntries >= 17000, $"Expected ≥17,000 entries, got {totalEntries}");
    }
}
