using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Conditional integration tests for the THAW PS2 zone .tex decoder.
///     When a Hollywood diagnostic sample already exists under the repo's output area,
///     these tests verify that the public decode path matches the decompilation-driven
///     prepared-source decode, with upload snapshots used only as fallback.
/// </summary>
public class ThawZoneTexFileTests
{
    private static string? ZoneTexPath => FindZoneTexFile();

    private static bool HasZoneTexData => ZoneTexPath != null;

    private static string? FindZoneTexFile()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var testOutputDir = Path.Combine(dir, "TestOutput");
            if (Directory.Exists(testOutputDir))
            {
                var candidate = Directory.EnumerateFiles(testOutputDir, "0009BF70.tex", SearchOption.AllDirectories)
                    .OrderBy(static path => path.Length)
                    .FirstOrDefault();
                if (candidate != null)
                    return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return null;
    }

    private static byte[] LoadZoneTexData()
    {
        Assert.SkipWhen(!HasZoneTexData, "Hollywood zone TEX diagnostic sample not available");
        return File.ReadAllBytes(ZoneTexPath!);
    }

    private static List<ThawZoneTexFile.ZoneTexHeaderEntry> GetRepresentativeEntries(
        List<ThawZoneTexFile.ZoneTexHeaderEntry> entries)
    {
        var firstEntry = entries[0];
        var psmt8Entry = entries.First(entry => ((entry.Tex0 >> 20) & 0x3F) == Ps2TexPixelDecoder.PSMT8);
        var psmct32Entry = entries.First(entry => ((entry.Tex0 >> 20) & 0x3F) == Ps2TexPixelDecoder.PSMCT32);
        var latePsmt4Entry = entries.Last(entry =>
            ((entry.Tex0 >> 20) & 0x3F) == Ps2TexPixelDecoder.PSMT4 &&
            entry.Checksum != firstEntry.Checksum);

        return new[] { firstEntry, psmt8Entry, psmct32Entry, latePsmt4Entry }
            .DistinctBy(static entry => entry.Checksum)
            .ToList();
    }

    private static void AssertEquivalentTextures(
        Dictionary<uint, Ps2Texture> expectedByChecksum,
        Dictionary<uint, Ps2Texture> actualByChecksum,
        IEnumerable<ThawZoneTexFile.ZoneTexHeaderEntry> entries)
    {
        foreach (var checksum in entries.Select(static entry => entry.Checksum))
        {
            Assert.True(expectedByChecksum.TryGetValue(checksum, out var expected),
                $"Missing expected texture 0x{checksum:X8}");
            Assert.True(actualByChecksum.TryGetValue(checksum, out var actual),
                $"Missing actual texture 0x{checksum:X8}");

            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
            Assert.Equal(expected.Psm, actual.Psm);
            Assert.Equal(expected.Cpsm, actual.Cpsm);
            Assert.Equal(expected.Pixels, actual.Pixels);
        }
    }

    private static Dictionary<uint, Ps2Texture> BuildPreparedSourceExpectedTextures(
        byte[] data,
        IReadOnlyList<ThawZoneTexFile.VramUpload> uploads,
        IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry> entries)
    {
        var expectedByChecksum = ThawZoneTexFile.DecodeFromHeaderDataSlots(data, uploads, entries)
            .ToDictionary(static texture => texture.Checksum);

        var unresolvedEntries = entries
            .Where(entry => !expectedByChecksum.ContainsKey(entry.Checksum))
            .ToList();
        if (unresolvedEntries.Count > 0)
        {
            foreach (var texture in ThawZoneTexFile.DecodeFromHeaderEntries(uploads, unresolvedEntries))
                expectedByChecksum.TryAdd(texture.Checksum, texture);
        }

        return expectedByChecksum;
    }

    [Fact]
    public void IsThawZoneTex_DetectsZoneTexFile()
    {
        var data = LoadZoneTexData();
        Assert.True(ThawZoneTexFile.IsThawZoneTex(data));
    }

    [Fact]
    public void IsThawZoneTex_RejectsStandardTexFile()
    {
        // A standard TEX v3 file header: version=3, numTextures=1
        var fakeData = new byte[0x200];
        BitConverter.TryWriteBytes(fakeData, (uint)3);
        Assert.False(ThawZoneTexFile.IsThawZoneTex(fakeData));
    }

    [Fact]
    public void ParseHeaderEntries_DiscoverCorrectRecordCount()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        // z_ho zone .tex has 990 records (verified by Python diagnostic)
        Assert.Equal(990, entries.Count);
    }

    [Fact]
    public void ParseHeaderEntries_RecordsHaveValidChecksums()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        // All checksums should be non-zero and > 0xFFFF (QbKey hashes)
        foreach (var checksum in entries.Select(static entry => entry.Checksum))
        {
            Assert.True(checksum > 0xFFFF,
                $"Record checksum 0x{checksum:X8} is suspiciously small");
        }
    }

    [Fact]
    public void ParseHeaderEntries_PsmDistributionMatchesExpected()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        var psmCounts = entries.GroupBy(e => (uint)((e.Tex0 >> 20) & 0x3F))
            .ToDictionary(g => g.Key, g => g.Count());

        // Expected: 962 PSMT4, 27 PSMT8, 1 PSMCT32
        Assert.Equal(962, psmCounts.GetValueOrDefault(Ps2TexPixelDecoder.PSMT4));
        Assert.Equal(27, psmCounts.GetValueOrDefault(Ps2TexPixelDecoder.PSMT8));
        Assert.Equal(1, psmCounts.GetValueOrDefault(Ps2TexPixelDecoder.PSMCT32));
    }

    [Fact]
    public void ParseHeaderEntries_RecordsHaveGroupChecksum()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        // ~20 distinct group checksums in z_ho
        var distinctGroups = entries.Select(e => e.GroupChecksum).Distinct().Count();
        Assert.True(distinctGroups >= 10 && distinctGroups <= 30,
            $"Expected ~20 distinct group checksums, got {distinctGroups}");
    }

    [Fact]
    public void ParseHeaderEntries_CumulativeOffsetSet()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        // For paletted textures, cumul_off should equal data_offset + pal_bytes
        // (except PSMCT32 edge case where data_offset=0)
        var palettedEntries = entries.Where(e =>
        {
            var psm = (uint)((e.Tex0 >> 20) & 0x3F);
            return psm is Ps2TexPixelDecoder.PSMT4 or Ps2TexPixelDecoder.PSMT8;
        }).ToList();

        foreach (var entry in palettedEntries)
        {
            Assert.Equal(entry.DataOffset + entry.PaletteBytes, entry.CumulativeOffset);
        }
    }

    [Fact]
    public void DecodeAllFromFile_ProducesTexturesForAllUniqueChecksums()
    {
        var data = LoadZoneTexData();
        var textures = ThawZoneTexFile.DecodeAllFromFile(data);

        // 990 records with 137 shared data blocks = 853 unique data offsets
        // Should produce at least 800 unique textures
        Assert.True(textures.Count >= 800,
            $"Expected at least 800 unique textures, got {textures.Count}");
    }

    [Fact]
    public void DecodeAllFromFile_MatchesPreparedSourceDecode_ForRepresentativeEntries()
    {
        var data = LoadZoneTexData();
        var uploads = ThawZoneTexFile.ParseVramUploads(data);
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);
        var representativeEntries = GetRepresentativeEntries(entries);

        var publicByChecksum = ThawZoneTexFile.DecodeAllFromFile(data)
            .ToDictionary(static texture => texture.Checksum);
        var preparedByChecksum = BuildPreparedSourceExpectedTextures(data, uploads, representativeEntries);

        AssertEquivalentTextures(preparedByChecksum, publicByChecksum, representativeEntries);
    }

    [Fact]
    public void DecodeAllFromFile_Psmct32TextureDecodes()
    {
        var data = LoadZoneTexData();
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);
        var ct32Entry = entries.First(e => ((e.Tex0 >> 20) & 0x3F) == Ps2TexPixelDecoder.PSMCT32);

        var textures = ThawZoneTexFile.DecodeAllFromFile(data);
        var ct32Texture = textures.FirstOrDefault(t => t.Checksum == ct32Entry.Checksum);

        Assert.NotNull(ct32Texture);
        Assert.Equal(64, ct32Texture.Width);
        Assert.Equal(64, ct32Texture.Height);
        Assert.NotNull(ct32Texture.Pixels);
    }

    [Fact]
    public void DecodeFromHeaderEntries_WithEmptyUploads_DerivesUploadsFromFileData()
    {
        var data = LoadZoneTexData();
        var uploads = ThawZoneTexFile.ParseVramUploads(data);
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);
        var representativeEntries = GetRepresentativeEntries(entries);

        var derivedByChecksum = ThawZoneTexFile.DecodeFromHeaderEntries(data, [], representativeEntries)
            .ToDictionary(static texture => texture.Checksum);
        var explicitByChecksum = ThawZoneTexFile.DecodeFromHeaderEntries(data, uploads, representativeEntries)
            .ToDictionary(static texture => texture.Checksum);

        AssertEquivalentTextures(explicitByChecksum, derivedByChecksum, representativeEntries);
    }

    [Fact]
    public void DecodeFromHeaderEntries_UploadsOnly_MatchesExplicitUploadSnapshotDecode()
    {
        var data = LoadZoneTexData();
        var uploads = ThawZoneTexFile.ParseVramUploads(data);
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);
        var representativeEntries = GetRepresentativeEntries(entries);

        var uploadsOnlyByChecksum = ThawZoneTexFile.DecodeFromHeaderEntries(uploads, representativeEntries)
            .ToDictionary(static texture => texture.Checksum);
        var requests = ThawZoneTexTextureCache.BuildDecodeRequests(uploads, representativeEntries);
        var snapshotByChecksum = ThawZoneTexTextureCache.DecodeTextureCache(uploads, requests, null);

        AssertEquivalentTextures(snapshotByChecksum, uploadsOnlyByChecksum, representativeEntries);
    }
}