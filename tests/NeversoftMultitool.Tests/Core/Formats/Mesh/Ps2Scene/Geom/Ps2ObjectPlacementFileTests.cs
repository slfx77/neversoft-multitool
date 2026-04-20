using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2ObjectPlacementFileTests(TestPaths paths)
{
    [Fact]
    public void TryParse_FittingZBhFile_ParsesTwoBlocksCountTwoEach()
    {
        var data = LoadPlacementData("z_bh", "00015410.91E1028D");

        Assert.True(Ps2ObjectPlacementFile.TryParse(data, out var file, out var skip));
        Assert.NotNull(file);
        Assert.Equal(string.Empty, skip);
        Assert.Equal(2, file!.Blocks.Count);
        Assert.All(file.Blocks, b => Assert.Equal(2, b.Items.Count));
    }

    [Fact]
    public void TryParse_FittingZBhFile_ThreeBlocksLastCountOne()
    {
        var data = LoadPlacementData("z_bh", "00026D20.91E1028D");

        Assert.True(Ps2ObjectPlacementFile.TryParse(data, out var file, out _));
        Assert.NotNull(file);
        Assert.Equal(3, file!.Blocks.Count);
        Assert.Equal(2, file.Blocks[0].Items.Count);
        Assert.Equal(2, file.Blocks[1].Items.Count);
        Assert.Single(file.Blocks[2].Items);
    }

    [Fact]
    public void TryParse_FittingFile_BboxHasValidMinMaxOnAllAxes()
    {
        var data = LoadPlacementData("z_bh", "00026D20.91E1028D");

        Assert.True(Ps2ObjectPlacementFile.TryParse(data, out var file, out _));
        Assert.NotNull(file);
        // Item 0 of each block carries the world-space AABB.
        foreach (var block in file!.Blocks)
        {
            var item0 = block.Items[0];
            Assert.True(item0.BboxMin.X <= item0.BboxMax.X,
                $"min.X ({item0.BboxMin.X}) <= max.X ({item0.BboxMax.X})");
            Assert.True(item0.BboxMin.Y <= item0.BboxMax.Y,
                $"min.Y ({item0.BboxMin.Y}) <= max.Y ({item0.BboxMax.Y})");
            Assert.True(item0.BboxMin.Z <= item0.BboxMax.Z,
                $"min.Z ({item0.BboxMin.Z}) <= max.Z ({item0.BboxMax.Z})");
        }
    }

    [Fact]
    public void TryParse_OutlierBulkFloatFile_ReturnsFalseWithSkipReason()
    {
        // 000516F0.91E1028D in z_bh is ~160KB of dense float data with no preamble signatures.
        // The phase400 RE classifies this as a non-placement-record sub-format.
        var data = LoadPlacementData("z_bh", "000516F0.91E1028D");

        var parsed = Ps2ObjectPlacementFile.TryParse(data, out var file, out var skip);
        Assert.False(parsed);
        Assert.Null(file);
        Assert.NotEmpty(skip);
    }

    private byte[] LoadPlacementData(string pakStem, string entryName)
    {
        var existing = TryGetExtractedEntry(pakStem, entryName);
        if (existing != null)
            return File.ReadAllBytes(existing);

        Assert.SkipWhen(!paths.HasSampleBuilds, "THAW PAK samples not available");

        var pakDir = Path.Combine(paths.SampleBuildsDir!,
            "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "PAK");
        var pakPath = Path.Combine(pakDir, pakStem + ".pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), $"PAK not found: {pakPath}");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Placement_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);

            var candidate = Path.Combine(tempDir, pakStem + ".pak", entryName);
            Assert.SkipWhen(!File.Exists(candidate), $"placement entry not extracted: {entryName}");
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
