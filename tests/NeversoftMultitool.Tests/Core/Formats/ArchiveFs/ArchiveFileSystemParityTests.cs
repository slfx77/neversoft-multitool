using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

/// <summary>
///     Corpus sweeps proving the filesystem layer lists exactly what the legacy
///     per-format readers list, and that the handle-based reads work on real
///     multi-gigabyte containers.
/// </summary>
public class ArchiveFileSystemParityTests(TestPaths paths)
{
    [CorpusFact]
    public void EntryLists_MatchLegacyReaders_AcrossSampleArchives()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var archives = Directory
            .EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
            .Where(f => ArchiveTypeDetector.GetArchiveExtension(f)
                is ".wad" or ".pre" or ".prx" or ".prd" or ".prf" or ".prg" or ".pkr" or ".pak" or ".apk")
            .ToList();
        Assert.SkipWhen(archives.Count == 0, "No archives in sample builds");

        var checkedCount = 0;
        var mismatches = new List<string>();

        foreach (var path in archives)
        {
            using var fs = ArchiveFileSystem.TryOpen(path);
            var legacy = TryLegacyList(path);

            if (fs == null)
            {
                // The factory refuses what the legacy path also refuses (raw paks,
                // WADs without .HED, unparseable tables).
                if (legacy is { Count: > 0 })
                    mismatches.Add($"{path}: factory refused but legacy listed {legacy.Count}");
                continue;
            }

            checkedCount++;
            if (legacy == null)
            {
                mismatches.Add($"{path}: factory listed {fs.Entries.Count} but legacy threw");
                continue;
            }

            if (fs.Entries.Count != legacy.Count)
            {
                mismatches.Add($"{path}: entry count {fs.Entries.Count} != legacy {legacy.Count}");
                continue;
            }

            for (var i = 0; i < legacy.Count; i++)
            {
                var a = fs.Entries[i];
                var b = legacy[i];
                if (a.FullName != b.FullName || a.Size != b.Size || a.Offset != b.Offset)
                {
                    mismatches.Add(
                        $"{path}[{i}]: {a.FullName}/{a.Size}/{a.Offset} != {b.FullName}/{b.Size}/{b.Offset}");
                    break;
                }
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} parity mismatch(es) across {checkedCount} archives:\n" +
            string.Join("\n", mismatches.Take(20)));
        Assert.True(checkedCount > 50, $"Expected a real corpus sweep, only {checkedCount} archives opened");
    }

    [CorpusFact]
    public void Open_ProjectEightDatapWad_ReadsTailEntryPast2Gb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var wadPath = Directory
            .EnumerateFiles(paths.SampleBuildsDir!, "DATAP.WAD", SearchOption.AllDirectories)
            .FirstOrDefault(f => new FileInfo(f).Length > int.MaxValue);
        Assert.SkipWhen(wadPath == null, "No >2GB DATAP.WAD in sample builds");

        // The old backend's File.ReadAllBytes threw for this container.
        using var fs = ArchiveFileSystem.TryOpen(wadPath!);
        Assert.NotNull(fs);
        Assert.NotEmpty(fs!.Entries);

        // The deepest addressable entry in this dump sits at ~2.145 GB — past
        // anything File.ReadAllBytes could have served (the old backend threw on
        // OPEN for this container), proving the long-offset RandomAccess path.
        var tail = fs.Entries
            .Where(e => e.Size > 0 && !e.IsCompressed && e.Offset + e.Size <= new FileInfo(wadPath!).Length)
            .OrderByDescending(e => e.Offset)
            .First();
        Assert.True(tail.Offset > 1_500_000_000, $"Expected a deep tail entry, got offset {tail.Offset}");

        var bytes = fs.ReadEntry(tail);
        Assert.Equal(tail.Size, bytes.Length);

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        Assert.Equal(fs.Entries.Count, backend!.Entries.Count);
    }

    [CorpusFact]
    public void Skate3Wad_ExposesLevelGeometryInsideNestedPres()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var wadPath = Directory
            .EnumerateFiles(paths.SampleBuildsDir!, "SKATE3.WAD", SearchOption.AllDirectories)
            .FirstOrDefault(f => File.Exists(WadArchive.GetHedPath(f)));
        Assert.SkipWhen(wadPath == null, "No SKATE3.WAD in sample builds");

        using var fs = ArchiveFileSystem.TryOpen(wadPath!);
        Assert.NotNull(fs);

        var nestedBsps = 0;
        var nestedSkns = 0;
        foreach (var entry in fs!.Entries)
        {
            if (ArchiveTypeDetector.GetArchiveExtension(entry.Name) is not (".pre" or ".prx"))
                continue;

            using var nested = fs.TryOpenNested(entry);
            if (nested == null)
                continue;

            foreach (var inner in nested.Entries)
            {
                if (inner.Name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                {
                    nestedBsps++;
                    // Prove the payload is readable, not just listed.
                    var bytes = nested.ReadEntry(inner);
                    Assert.Equal(inner.Size, bytes.Length);
                }
                else if (inner.Name.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                {
                    nestedSkns++;
                }
            }
        }

        Assert.True(nestedBsps > 0, "Expected level .bsp files inside SKATE3.WAD's nested PREs");
        Assert.True(nestedSkns > 0, "Expected character .skn files inside SKATE3.WAD's nested PREs");
    }

    private static List<ArchiveEntry>? TryLegacyList(string path)
    {
        try
        {
            return ArchiveTypeDetector.GetArchiveExtension(path) switch
            {
                ".wad" => File.Exists(WadArchive.GetHedPath(path)) ? WadArchive.GetFileList(path) : null,
                ".prx" => CompressedPreArchive.GetFileList(path),
                ".pre" or ".prd" or ".prf" or ".prg" => CompressedPreArchive.IsCompressedPre(path)
                    ? CompressedPreArchive.GetFileList(path)
                    : PreArchive.GetFileList(path),
                ".pkr" => PkrArchive.GetFileList(path),
                ".pak" or ".apk" => PakArchive.IsPakArchive(path) ? PakArchive.GetFileList(path) : null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}