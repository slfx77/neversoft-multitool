using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CutArchiveTests(TestPaths paths)
{
    private const string ThugPs2Build = "Tony Hawk's Underground (2003-10-2, PS2 - Final)";
    private const string Thug2Ps2Build = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string Thug2WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    private const uint ExtQb = 0x2BBEA5C3;
    private const uint ExtTex = 0x1512808D;

    [Fact]
    public void GetFileList_SyntheticCut_MapsExtensionsAndPlatformSuffixes()
    {
        var tempCut = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + ".cut.ps2");
        try
        {
            using (var stream = File.Create(tempCut))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); // version
                writer.Write(2); // numFiles
                // entry 0: QB singleton (nameKey 0) at 8 + 2*16 = 40
                writer.Write(40);
                writer.Write(8);
                writer.Write(0u);
                writer.Write(ExtQb);
                // entry 1: TEX with an unresolvable name key
                writer.Write(48);
                writer.Write(4);
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtTex);
                writer.Write(new byte[12]);
            }

            Assert.True(CutArchive.IsCut(tempCut));
            var entries = CutArchive.GetFileList(tempCut);
            Assert.Equal(2, entries.Count);
            Assert.Equal("cutscene.qb", entries[0].Name); // zero-key singleton named by role
            Assert.Equal("deadbeef.tex.ps2", entries[1].Name); // hex fallback + platform suffix
        }
        finally
        {
            if (File.Exists(tempCut))
                File.Delete(tempCut);
        }
    }

    [Fact]
    public void IsCut_NonContiguousOrTruncated_ReturnsFalse()
    {
        var tempCut = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + ".cut");
        try
        {
            // Non-contiguous: entry data offset leaves a gap
            using (var stream = File.Create(tempCut))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1);
                writer.Write(1);
                writer.Write(32); // should be 24
                writer.Write(4);
                writer.Write(0u);
                writer.Write(ExtQb);
                writer.Write(new byte[12]);
            }

            Assert.False(CutArchive.IsCut(tempCut));

            // Truncated: blobs claim to extend past EOF
            using (var stream = File.Create(tempCut))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1);
                writer.Write(1);
                writer.Write(24);
                writer.Write(1000);
                writer.Write(0u);
                writer.Write(ExtQb);
                writer.Write(new byte[4]);
            }

            Assert.False(CutArchive.IsCut(tempCut));
        }
        finally
        {
            if (File.Exists(tempCut))
                File.Delete(tempCut);
        }
    }

    [Fact]
    public void GetFileList_Thug2XboxSample_PlatformTypedEntriesEndXbx()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var cut = paths.FindSampleFiles(Thug2XboxBuild, "*.cut.xbx").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(cut == null, "No .cut.xbx found in THUG2 Xbox build");

        Assert.True(CutArchive.IsCut(cut!));
        var entries = CutArchive.GetFileList(cut!);
        Assert.NotEmpty(entries);

        var platformTyped = entries.Where(e =>
            e.Name.Contains(".tex", StringComparison.Ordinal) ||
            e.Name.Contains(".skin", StringComparison.Ordinal) ||
            e.Name.Contains(".mdl", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(platformTyped);
        Assert.All(platformTyped, e => Assert.EndsWith(".xbx", e.Name));

        // Full coverage: entry sizes account for the data region minus alignment padding
        var dataBytes = new FileInfo(cut!).Length - 8 - entries.Count * 16;
        var payload = entries.Sum(e => e.Size);
        Assert.InRange(dataBytes - payload, 0, entries.Count * 8L);
    }

    [Fact]
    public void GetFileList_ThugBareCut_NoPlatformSuffixes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var cut = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Where(p => p.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(cut == null, "No bare .cut found in THUG PS2 build");

        Assert.True(CutArchive.IsCut(cut!));
        var entries = CutArchive.GetFileList(cut!);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(
            e.Name.EndsWith(".ps2", StringComparison.Ordinal) ||
            e.Name.EndsWith(".xbx", StringComparison.Ordinal),
            $"bare .cut entry '{e.Name}' must not carry a platform suffix"));
    }

    [Fact]
    public void ExtractFiles_Thug2Ps2Sample_WritesAllEntriesAndManifest()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var cut = paths.FindSampleFiles(Thug2Ps2Build, "*.cut.ps2").OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.SkipWhen(cut == null, "No .cut.ps2 found in THUG2 PS2 build");

        var entries = CutArchive.GetFileList(cut!);
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Cut_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CutArchive.ExtractFiles(cut!, tempDir, null, TestContext.Current.CancellationToken);

            var stem = ArchiveNaming.GetExtractionStem(cut!);
            foreach (var entry in entries)
            {
                var extractedPath = Path.Combine(tempDir, stem, entry.Name);
                Assert.True(File.Exists(extractedPath), $"missing {entry.Name}");
                Assert.Equal(entry.Size, new FileInfo(extractedPath).Length);
            }

            var manifest = Path.Combine(tempDir, stem + ".cif.json");
            Assert.True(File.Exists(manifest), "cif.json manifest missing");
            var json = File.ReadAllText(manifest);
            Assert.Contains("\"files\"", json);

            // THUG2 cuts carry a cifstruct (CStruct WriteToBuffer stream) instead of the
            // THUG CIF v1 table; the manifest must decode it into the object list.
            Assert.Contains("\"objects\"", json);
            Assert.Contains("\"camAnimDuration\"", json);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileList_Ps2XboxPair_DiffersOnlyByGeometryKeySwap()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Same-stem THUG2 pair: TOCs are byte-identical except GEOM (PS2) <-> MDL (Xbox)
        var ps2Files = paths.FindSampleFiles(Thug2Ps2Build, "*.cut.ps2").ToList();
        var pair = ps2Files
            .Select(p => (Ps2: p, Xbox: paths.FindSampleFile(
                Thug2XboxBuild, Path.GetFileName(p).Replace(".cut.ps2", ".cut.xbx"))))
            .FirstOrDefault(t => t.Xbox != null);
        Assert.SkipWhen(pair.Ps2 == null || pair.Xbox == null, "No same-stem PS2/Xbox cut pair found");

        var ps2Entries = CutArchive.GetFileList(pair.Ps2!);
        var xboxEntries = CutArchive.GetFileList(pair.Xbox!);
        Assert.Equal(ps2Entries.Count, xboxEntries.Count);

        for (var i = 0; i < ps2Entries.Count; i++)
        {
            Assert.Equal(ps2Entries[i].Crc, xboxEntries[i].Crc); // same name keys in same order
            var ps2Type = ps2Entries[i].Name[(ps2Entries[i].Name.IndexOf('.') + 1)..];
            var xboxType = xboxEntries[i].Name[(xboxEntries[i].Name.IndexOf('.') + 1)..];
            if (ps2Type.StartsWith("geom", StringComparison.Ordinal))
                Assert.StartsWith("mdl", xboxType);
            else
                Assert.Equal(ps2Type.Replace(".ps2", ""), xboxType.Replace(".xbx", ""));
        }
    }

    [CorpusFact]
    public void GetFileList_AllCutFiles_ParseClean()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Concat(paths.FindSampleFiles(ThugPs2Build, "*.cut.ps2"))
            .Concat(paths.FindSampleFiles(Thug2Ps2Build, "*.cut.ps2"))
            .Concat(paths.FindSampleFiles(Thug2XboxBuild, "*.cut.xbx"))
            .Concat(paths.FindSampleFiles(Thug2WindowsBuild, "*.cut.xbx"))
            .Distinct()
            .ToList();
        Assert.SkipWhen(files.Count == 0, "No cut files found");

        var failures = new List<string>();
        var unknownKeys = new HashSet<uint>();
        foreach (var file in files)
        {
            try
            {
                Assert.True(CutArchive.IsCut(file), "structural probe rejected the file");
                var entries = CutArchive.GetFileList(file);
                Assert.NotEmpty(entries);
                foreach (var entry in entries)
                {
                    // Hex-extension fallback marks an unmapped section key
                    var ext = Path.GetExtension(entry.Name.Replace(".ps2", "").Replace(".xbx", ""));
                    if (ext.Length == 9 && uint.TryParse(ext[1..], System.Globalization.NumberStyles.HexNumber,
                            null, out var key))
                        unknownKeys.Add(key);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(215, files.Count);
        // Every section key in the corpus is mapped (0x508AE2F2 ships as .cif2, not a fallback)
        Assert.Empty(unknownKeys);
    }
}
