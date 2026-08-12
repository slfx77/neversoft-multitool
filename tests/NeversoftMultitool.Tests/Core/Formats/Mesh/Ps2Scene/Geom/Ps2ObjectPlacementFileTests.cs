using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2ObjectPlacementFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [Fact]
    public void TryParse_CanonicalZBh1256ByteRnb_FailsStatic()
    {
        AssertCanonicalRnbRejected(
            "z_bh", "000155D0.rnb", 1_256,
            "A7CD9840EFE15489CD4A9B4C3D1E2E7CC0EDB41FC08410E6715C2DB23C214B6A",
            1_110_237_086);
    }

    [Fact]
    public void TryParse_CanonicalZBh776ByteRnb_FailsStatic()
    {
        AssertCanonicalRnbRejected(
            "z_bh", "00027020.rnb", 776,
            "42E70FAFB91BE04D46E0DEA6E9E5EF896FECCC7BBD44B9E05028682A925E8589",
            1_112_164_405);
    }

    [Fact]
    public void TryParse_CanonicalZHo776ByteRnb_FailsStatic()
    {
        AssertCanonicalRnbRejected(
            "z_ho", "00017100.rnb", 776,
            "8094275170320922070C77C81855296848EDE890470E11CD8A53387F0AC9DD87",
            1_110_069_753);
    }

    [Fact]
    public void TryParse_CanonicalZBh162728ByteRnb_FailsStatic()
    {
        AssertCanonicalRnbRejected(
            "z_bh", "00052430.rnb", 162_728,
            "6A72FFF4669B11C28EC94507D2A9C1ABA9DF58D3785356B2BD1AF497A6BBD5EF",
            3_271_758_116);
    }

    private void AssertCanonicalRnbRejected(
        string pakStem,
        string entryName,
        int expectedLength,
        string expectedSha256,
        uint expectedLegacyCount)
    {
        var data = LoadPlacementData(pakStem, entryName);
        Assert.Equal(expectedLength, data.Length);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(data)));

        var parsed = Ps2ObjectPlacementFile.TryParse(data, out var file, out var skip);
        Assert.False(parsed);
        Assert.Null(file);
        Assert.Equal(
            $"first block count {expectedLegacyCount} exceeds sanity limit at +0xC",
            skip);
    }

    private byte[] LoadPlacementData(string pakStem, string entryName)
    {
        var existing = TryGetExtractedEntry(pakStem, entryName);
        if (existing != null)
            return File.ReadAllBytes(existing);

        var pakPath = paths.FindSampleFile(BuildName, pakStem + ".pak.ps2");
        Assert.SkipWhen(pakPath is null, $"{pakStem}.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Placement_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath!, tempDir, token: TestContext.Current.CancellationToken);

            var candidate = Path.Combine(tempDir, pakStem + ".pak", entryName);
            Assert.True(File.Exists(candidate), $"placement entry not extracted: {entryName}");
            return File.ReadAllBytes(candidate);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private string? TryGetExtractedEntry(string pakStem, string entryName)
    {
        if (paths.TestOutputDir == null)
            return null;

        // Pre-extracted cache under tests/TestOutput/thaw_ps2_mdl_review/extracted/...
        var cached = Path.Combine(
            paths.TestOutputDir, "thaw_ps2_mdl_review", "extracted",
            pakStem + "_pak", pakStem + ".pak", entryName);
        if (File.Exists(cached))
            return cached;

        // Older / alt cache layout (matches Ps2MdlPreambleTests.TryGetExtractedMdl).
        var alt = Path.Combine(paths.TestOutputDir, pakStem + "_pak", pakStem + ".pak", entryName);
        return File.Exists(alt) ? alt : null;
    }
}
