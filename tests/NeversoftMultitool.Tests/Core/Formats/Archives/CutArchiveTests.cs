using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Cas;
using NeversoftMultitool.Core.Formats.Wgt;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public class CutArchiveTests(TestPaths paths)
{
    private const string ThugPs2Build = "Tony Hawk's Underground (2003-10-2, PS2 - Final)";
    private const string Thug2Ps2Build = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string Thug2WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    private const uint ExtQb = 0x2BBEA5C3;
    private const uint ExtCif = 0x5AC14717;
    private const uint ExtTex = 0x1512808D;
    private const uint ExtCas = 0xFFC529F4;
    private const uint ExtWgt = 0x2CD4107D;

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
                writer.Write(4); // numFiles
                // entry 0: QB singleton (nameKey 0) at 8 + 4*16 = 72
                writer.Write(72);
                writer.Write(8);
                writer.Write(0u);
                writer.Write(ExtQb);
                // entry 1: TEX with an unresolvable name key
                writer.Write(80);
                writer.Write(4);
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtTex);
                // entry 2: CAS with the same unresolvable name key
                writer.Write(84);
                writer.Write(12);
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtCas);
                // entry 3: WGT with the same unresolvable name key
                writer.Write(96);
                writer.Write(8);
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtWgt);
                writer.Write(new byte[24]);
                writer.Write(1u); // WGT version
                writer.Write(0); // vertex count
            }

            Assert.True(CutArchive.IsCut(tempCut));
            var entries = CutArchive.GetFileList(tempCut);
            Assert.Equal(4, entries.Count);
            Assert.Equal("cutscene.qb", entries[0].Name); // zero-key singleton named by role
            Assert.Equal("deadbeef.tex.ps2", entries[1].Name); // hex fallback + platform suffix
            Assert.Equal("deadbeef.cas.ps2", entries[2].Name); // CAS dialect comes from the container
            Assert.Equal("deadbeef.wgt.ps2", entries[3].Name); // WGT provenance comes from the container
        }
        finally
        {
            if (File.Exists(tempCut))
                File.Delete(tempCut);
        }
    }

    [Theory]
    [InlineData(".cut.ps2", "deadbeef.cas.ps2")]
    [InlineData(".cut.xbx", "deadbeef.cas.xbx")]
    [InlineData(".cut", "deadbeef.cas")]
    [InlineData(".cut.ngc", "deadbeef.cas.ngc")]
    public void GetFileList_SyntheticCas_PreservesContainerPlatformSuffix(
        string cutSuffix, string expectedEntryName)
    {
        var tempCut = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + cutSuffix);
        try
        {
            using (var stream = File.Create(tempCut))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); // CUT version
                writer.Write(1); // file count
                writer.Write(24); // payload immediately after the TOC
                writer.Write(12); // empty CAS v2 header
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtCas);
                writer.Write(2u); // CAS version
                writer.Write(0u); // removal mask
                writer.Write(0); // entry count
            }

            Assert.True(CutArchive.IsCut(tempCut));
            Assert.Equal(expectedEntryName, Assert.Single(CutArchive.GetFileList(tempCut)).Name);
        }
        finally
        {
            if (File.Exists(tempCut))
                File.Delete(tempCut);
        }
    }

    [Theory]
    [InlineData(".cut.ps2", "deadbeef.wgt.ps2")]
    [InlineData(".cut.xbx", "deadbeef.wgt.xbx")]
    [InlineData(".cut", "deadbeef.wgt")]
    [InlineData(".cut.ngc", "deadbeef.wgt.ngc")]
    public void GetFileList_SyntheticWgt_PreservesContainerPlatformSuffix(
        string cutSuffix, string expectedEntryName)
    {
        var tempCut = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_" + Guid.NewGuid().ToString("N")[..8] + cutSuffix);
        try
        {
            using (var stream = File.Create(tempCut))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); // CUT version
                writer.Write(1); // file count
                writer.Write(24); // payload immediately after the TOC
                writer.Write(8); // empty compiled WGT v1 header
                writer.Write(0xDEADBEEFu);
                writer.Write(ExtWgt);
                writer.Write(1u); // WGT version
                writer.Write(0); // vertex count
            }

            Assert.True(CutArchive.IsCut(tempCut));
            Assert.Equal(expectedEntryName, Assert.Single(CutArchive.GetFileList(tempCut)).Name);
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

        string? cut = null;
        List<ArchiveEntry>? entries = null;
        foreach (var candidate in paths.FindSampleFiles(Thug2XboxBuild, "*.cut.xbx")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var candidateEntries = CutArchive.GetFileList(candidate);
            if (!candidateEntries.Any(static entry =>
                    entry.Name.EndsWith(".cas.xbx", StringComparison.OrdinalIgnoreCase)))
                continue;

            cut = candidate;
            entries = candidateEntries;
            break;
        }

        Assert.SkipWhen(cut == null || entries == null, "No CAS-bearing .cut.xbx found in THUG2 Xbox build");
        var selectedEntries = entries!;
        Assert.True(CutArchive.IsCut(cut!));
        Assert.NotEmpty(selectedEntries);
        Assert.Contains(selectedEntries, static entry =>
            entry.Name.EndsWith(".cas.xbx", StringComparison.OrdinalIgnoreCase));

        var platformTyped = selectedEntries.Where(e =>
            e.Name.Contains(".tex", StringComparison.Ordinal) ||
            e.Name.Contains(".skin", StringComparison.Ordinal) ||
            e.Name.Contains(".mdl", StringComparison.Ordinal) ||
            e.Name.Contains(".cas", StringComparison.Ordinal) ||
            e.Name.Contains(".wgt", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(platformTyped);
        Assert.All(platformTyped, e => Assert.EndsWith(".xbx", e.Name));

        // Full coverage: entry sizes account for the data region minus alignment padding
        var dataBytes = new FileInfo(cut!).Length - 8 - selectedEntries.Count * 16;
        var payload = selectedEntries.Sum(e => e.Size);
        Assert.InRange(dataBytes - payload, 0, selectedEntries.Count * 8L);
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
    public void ExtractFiles_CifCountWhoseSerializedSizeWraps_IsIgnored()
    {
        const int wrappedCount = 214_748_365;
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_Test_Cut_CifOverflow_" + Guid.NewGuid().ToString("N")[..8]);
        var cutPath = Path.Combine(tempDir, "wrapped.cut");
        var outputDir = Path.Combine(tempDir, "output");

        try
        {
            Directory.CreateDirectory(tempDir);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
            {
                writer.Write(1); // CUT version
                writer.Write(1); // file count
                writer.Write(24); // payload immediately after the one-entry TOC
                writer.Write(12); // wrapped 8 + count * 20 result under 32-bit arithmetic
                writer.Write(0u); // singleton name key
                writer.Write(ExtCif);
                writer.Write(1u); // CIF version
                writer.Write(wrappedCount);
                writer.Write(0u); // completes the 12-byte malformed payload
            }

            var cutData = stream.ToArray();
            Assert.Equal(36, cutData.Length);
            File.WriteAllBytes(cutPath, cutData);

            Assert.True(CutArchive.IsCut(cutPath));
            CutArchive.ExtractFiles(
                cutPath, outputDir, null, TestContext.Current.CancellationToken);

            var manifestPath = Path.Combine(outputDir, "wrapped.cif.json");
            Assert.True(File.Exists(manifestPath));
            Assert.DoesNotContain("\"objects\":", File.ReadAllText(manifestPath));
            Assert.Equal(
                12,
                new FileInfo(Path.Combine(outputDir, "wrapped", "objects.cif")).Length);
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
                    if (ext.Length == 9 && uint.TryParse(ext[1..], NumberStyles.HexNumber,
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

    [CorpusFact]
    public void CasMembers_TypedCutCorpus_ParseStrictlyAndPinDialectCounts()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var bare = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Where(static path => path.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var ps2 = paths.FindSampleFiles(ThugPs2Build, "*.cut.ps2")
            .Concat(paths.FindSampleFiles(Thug2Ps2Build, "*.cut.ps2"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var xbox = paths.FindSampleFiles(Thug2XboxBuild, "*.cut.xbx")
            .Concat(paths.FindSampleFiles(Thug2WindowsBuild, "*.cut.xbx"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(43, bare.Length);
        Assert.Equal(86, ps2.Length);
        Assert.Equal(86, xbox.Length);
        Assert.All(bare, path => Assert.DoesNotContain(
            CutArchive.GetFileList(path), static entry =>
                entry.Name.EndsWith(".cas", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(".cas.", StringComparison.OrdinalIgnoreCase)));

        var ps2Stats = AuditCasMembers(ps2, ".cas.ps2", CasPolyRemovalPlatform.Ps2);
        Assert.Equal(new CasCutStats(65, 548, 329440, 40358, 322), ps2Stats);

        var xboxStats = AuditCasMembers(xbox, ".cas.xbx", CasPolyRemovalPlatform.Xbox);
        Assert.Equal(new CasCutStats(48, 510, 232080, 18830, 340), xboxStats);

        Assert.Equal(1058, ps2Stats.MemberCount + xboxStats.MemberCount);
        Assert.Equal(561520, ps2Stats.ByteCount + xboxStats.ByteCount);
        Assert.Equal(59188, ps2Stats.EntryCount + xboxStats.EntryCount);
        Assert.Equal(662, ps2Stats.ZeroEntryCount + xboxStats.ZeroEntryCount);
    }

    [CorpusFact]
    public void WgtVersion1Members_TypedCutCorpus_ParseStrictlyAndMatchLoosePayloads()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var loosePaths = paths.FindSampleFiles(ThugPs2Build, "*.wgt.ps2")
            .Concat(paths.FindSampleFiles(Thug2XboxBuild, "*.wgt.xbx"))
            .Concat(paths.FindSampleFiles(Thug2WindowsBuild, "*.wgt.xbx"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(12, loosePaths.Length);

        var loosePayloadSha256 = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in loosePaths)
        {
            var data = File.ReadAllBytes(path);
            var platform = path.EndsWith(".wgt.ps2", StringComparison.OrdinalIgnoreCase)
                ? CutsceneWeightMapPlatform.Ps2
                : CutsceneWeightMapPlatform.Xbox;
            var document = CutsceneWeightMapFile.Parse(data, platform);
            loosePayloadSha256.Add(document.SerializedSha256);
        }

        Assert.Equal(8, loosePayloadSha256.Count);

        var thug2Ps2 = paths.FindSampleFiles(Thug2Ps2Build, "*.cut.ps2")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rejectedVersion2 = 0;
        foreach (var cutPath in thug2Ps2)
        {
            Assert.True(CutArchive.IsCut(cutPath), $"CUT probe rejected {cutPath}");
            var data = File.ReadAllBytes(cutPath);
            foreach (var entry in CutArchive.GetFileList(cutPath).Where(static entry =>
                         entry.Name.EndsWith(".wgt.ps2", StringComparison.OrdinalIgnoreCase)))
            {
                var payload = data.AsSpan(
                    checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
                Assert.True(payload.Length >= 8);
                Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
                var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
                Assert.True(count >= 0);
                Assert.Equal(checked(8L + 19L * count), payload.Length);
                Assert.Throws<InvalidDataException>(() =>
                    CutsceneWeightMapFile.Parse(payload, CutsceneWeightMapPlatform.Ps2));
                rejectedVersion2++;
            }
        }

        Assert.Equal(40, rejectedVersion2);

        var bare = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Where(static path => path.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(43, bare.Length);
        Assert.All(bare, path => Assert.DoesNotContain(
            CutArchive.GetFileList(path), static entry =>
                entry.Name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(".wgt.", StringComparison.OrdinalIgnoreCase)));

        var ps2 = paths.FindSampleFiles(ThugPs2Build, "*.cut.ps2")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var xbox = paths.FindSampleFiles(Thug2XboxBuild, "*.cut.xbx")
            .Concat(paths.FindSampleFiles(Thug2WindowsBuild, "*.cut.xbx"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var ps2Stats = AuditWgtV1Members(
            ps2, ".wgt.ps2", CutsceneWeightMapPlatform.Ps2, loosePayloadSha256);
        Assert.Equal(new WgtCutStats(32, 132, 2590896, 172656, 4), ps2Stats);

        var xboxStats = AuditWgtV1Members(
            xbox, ".wgt.xbx", CutsceneWeightMapPlatform.Xbox, loosePayloadSha256);
        Assert.Equal(new WgtCutStats(20, 80, 1406140, 93700, 4), xboxStats);

        Assert.Equal(52, ps2Stats.ContainerCount + xboxStats.ContainerCount);
        Assert.Equal(212, ps2Stats.MemberCount + xboxStats.MemberCount);
        Assert.Equal(3997036, ps2Stats.ByteCount + xboxStats.ByteCount);
        Assert.Equal(266356, ps2Stats.VertexCount + xboxStats.VertexCount);
        Assert.Equal(loosePayloadSha256.Count,
            ps2Stats.UniquePayloadCount + xboxStats.UniquePayloadCount);
    }

    private static CasCutStats AuditCasMembers(
        IEnumerable<string> cutPaths,
        string expectedSuffix,
        CasPolyRemovalPlatform platform)
    {
        var containersWithCas = 0;
        var memberCount = 0;
        long byteCount = 0;
        long entryCount = 0;
        var zeroEntryCount = 0;

        foreach (var cutPath in cutPaths)
        {
            Assert.True(CutArchive.IsCut(cutPath), $"CUT probe rejected {cutPath}");
            var entries = CutArchive.GetFileList(cutPath);
            var casEntries = entries
                .Where(entry => entry.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.DoesNotContain(entries, entry =>
                (entry.Name.EndsWith(".cas", StringComparison.OrdinalIgnoreCase)
                 || entry.Name.Contains(".cas.", StringComparison.OrdinalIgnoreCase))
                && !entry.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));
            if (casEntries.Length == 0)
                continue;

            containersWithCas++;
            var data = File.ReadAllBytes(cutPath);
            foreach (var entry in casEntries)
            {
                var payload = data.AsSpan(checked((int)entry.Offset), checked((int)entry.Size));
                var document = CasPolyRemovalFile.Parse(payload, platform);
                memberCount++;
                byteCount += entry.Size;
                entryCount += document.Entries.Length;
                if (document.Entries.Length == 0)
                    zeroEntryCount++;
            }
        }

        return new CasCutStats(containersWithCas, memberCount, byteCount, entryCount, zeroEntryCount);
    }

    private static WgtCutStats AuditWgtV1Members(
        IEnumerable<string> cutPaths,
        string expectedSuffix,
        CutsceneWeightMapPlatform platform,
        HashSet<string> loosePayloadSha256)
    {
        var containersWithWgt = 0;
        var memberCount = 0;
        long byteCount = 0;
        long vertexCount = 0;
        var uniquePayloads = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cutPath in cutPaths)
        {
            Assert.True(CutArchive.IsCut(cutPath), $"CUT probe rejected {cutPath}");
            var entries = CutArchive.GetFileList(cutPath);
            var wgtEntries = entries
                .Where(entry => entry.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.DoesNotContain(entries, entry =>
                (entry.Name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase)
                 || entry.Name.Contains(".wgt.", StringComparison.OrdinalIgnoreCase))
                && !entry.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));
            if (wgtEntries.Length == 0)
                continue;

            containersWithWgt++;
            var data = File.ReadAllBytes(cutPath);
            foreach (var entry in wgtEntries)
            {
                var payload = data.AsSpan(checked((int)entry.Offset), checked((int)entry.Size));
                var document = CutsceneWeightMapFile.Parse(payload, platform);
                Assert.True(loosePayloadSha256.Contains(document.SerializedSha256),
                    $"CUT WGT payload {document.SerializedSha256} has no loose-file oracle");
                uniquePayloads.Add(document.SerializedSha256);
                memberCount++;
                byteCount += entry.Size;
                vertexCount += document.Vertices.Length;
            }
        }

        return new WgtCutStats(
            containersWithWgt,
            memberCount,
            byteCount,
            vertexCount,
            uniquePayloads.Count);
    }

    private readonly record struct CasCutStats(
        int ContainerCount,
        int MemberCount,
        long ByteCount,
        long EntryCount,
        int ZeroEntryCount);

    private readonly record struct WgtCutStats(
        int ContainerCount,
        int MemberCount,
        long ByteCount,
        long VertexCount,
        int UniquePayloadCount);
}
