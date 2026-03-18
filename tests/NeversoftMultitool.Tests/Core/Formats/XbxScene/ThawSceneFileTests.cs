using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class ThawSceneFileTests(TestPaths paths)
{
    private string SkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "SKIN");

    private string MdlDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "MDL");

    // ── IsThawScene ──

    [Fact]
    public void IsThawScene_WithThawFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "acc_backpack01.skin.wpc");
        Assert.SkipWhen(!File.Exists(file), "acc_backpack01.skin.wpc not found");

        Assert.True(ThawSceneFile.IsThawScene(File.ReadAllBytes(file)));
    }

    [Fact]
    public void IsThawScene_WithThug2File_ReturnsFalse()
    {
        // THUG2 version triple (1,1,1) at offset 0
        var data = new byte[48];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        Assert.False(ThawSceneFile.IsThawScene(data));
    }

    [Fact]
    public void IsThawScene_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawSceneFile.IsThawScene([]));
    }

    // ── Parse ──

    [Fact]
    public void Parse_ThawSkinFile_ProducesMeshes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "acc_backpack01.skin.wpc");
        Assert.SkipWhen(!File.Exists(file), "acc_backpack01.skin.wpc not found");

        var scene = ThawSceneFile.Parse(file);

        Assert.NotEmpty(scene.Materials);
        Assert.NotEmpty(scene.Sectors);
        Assert.True(scene.TotalTriangles > 0, "Expected triangles > 0");
        Assert.True(scene.TotalVertices > 0, "Expected vertices > 0");

        // THAW meshes are pre-triangulated
        foreach (var sector in scene.Sectors)
        foreach (var mesh in sector.Meshes)
            Assert.True(mesh.IsPreTriangulated);
    }

    [Fact]
    public void Parse_ThawMdlFile_ProducesMultipleSectors()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(MdlDir, "veh_sub.mdl.wpc");
        Assert.SkipWhen(!File.Exists(file), "veh_sub.mdl.wpc not found");

        var scene = ThawSceneFile.Parse(file);

        Assert.True(scene.Sectors.Length > 1, "MDL file should have multiple sectors");
        Assert.NotEmpty(scene.Materials);
        Assert.True(scene.TotalTriangles > 0);
    }

    [Fact]
    public void Parse_SkaterFile_HasReasonableStats()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "skater_hawk.skin.wpc");
        Assert.SkipWhen(!File.Exists(file), "skater_hawk.skin.wpc not found");

        var scene = ThawSceneFile.Parse(file);

        Assert.True(scene.TotalTriangles > 1000, $"Skater should have >1000 tris, got {scene.TotalTriangles}");
        Assert.True(scene.Materials.Length > 3, $"Skater should have >3 materials, got {scene.Materials.Length}");
    }

    [Fact]
    public void Parse_SkinnedSkater_PreservesSkinWeightsAndBoneIndices()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "skater_lasek.skin.wpc");
        Assert.SkipWhen(!File.Exists(file), "skater_lasek.skin.wpc not found");

        var scene = ThawSceneFile.Parse(file);
        var skinnedVertices = scene.Sectors
            .Where(sector => sector.IsSkinned)
            .SelectMany(sector => sector.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Where(vertex => vertex.HasSkinData)
            .ToArray();

        Assert.NotEmpty(skinnedVertices);
        Assert.Contains(skinnedVertices, vertex =>
            vertex.BoneIndex0 != 0 || vertex.BoneIndex1 != 0 || vertex.BoneIndex2 != 0 || vertex.BoneIndex3 != 0);
        Assert.All(skinnedVertices.Take(256), vertex =>
        {
            var sum = vertex.BoneWeight0 + vertex.BoneWeight1 + vertex.BoneWeight2 + vertex.BoneWeight3;
            Assert.InRange(sum, 0.999f, 1.001f);
        });
    }

    // ── Batch parse ──

    [Fact]
    public void BatchParse_AllThawSkinFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(SkinDir), "SKIN directory not found");

        var files = Directory.GetFiles(SkinDir, "*.skin.wpc");
        Assert.SkipWhen(files.Length == 0, "No .skin.wpc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = ThawSceneFile.Parse(f);
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} THAW SKIN files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }

    [Fact]
    public void BatchParse_AllThawMdlFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(MdlDir), "MDL directory not found");

        var files = Directory.GetFiles(MdlDir, "*.mdl.wpc");
        Assert.SkipWhen(files.Length == 0, "No .mdl.wpc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = ThawSceneFile.Parse(f);
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} THAW MDL files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
    }
}