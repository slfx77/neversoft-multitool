using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class XbxSceneFileTests(TestPaths paths)
{
    private string SkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)", "SKIN");

    private string MdlDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)", "MDL");

    // ── IsXbxScene ──

    [Fact]
    public void IsXbxScene_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "Anl_Pigeon.skin.xbx");
        Assert.SkipWhen(!File.Exists(file), "Anl_Pigeon.skin.xbx not found");

        Assert.True(XbxSceneFile.IsXbxScene(File.ReadAllBytes(file)));
    }

    [Fact]
    public void IsXbxScene_EmptyData_ReturnsFalse()
    {
        Assert.False(XbxSceneFile.IsXbxScene([]));
    }

    [Fact]
    public void IsXbxScene_WrongVersion_ReturnsFalse()
    {
        // Version (3,4,1) = PS2 format, not Xbox
        var data = new byte[12];
        BitConverter.GetBytes(3u).CopyTo(data, 0);
        BitConverter.GetBytes(4u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        Assert.False(XbxSceneFile.IsXbxScene(data));
    }

    // ── Parse known file ──

    [Fact]
    public void Parse_KnownFile_ReturnsValidScene()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "Board_Skaboto.skin.xbx");
        Assert.SkipWhen(!File.Exists(file), "Board_Skaboto.skin.xbx not found");

        var scene = XbxSceneFile.Parse(file);

        Assert.NotEmpty(scene.Materials);
        Assert.NotEmpty(scene.Sectors);
        Assert.True(scene.TotalTriangles > 0, "Expected triangles > 0");
        Assert.True(scene.TotalVertices > 0, "Expected vertices > 0");
    }

    [Fact]
    public void Parse_KnownFile_MaterialsHavePasses()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "Board_Skaboto.skin.xbx");
        Assert.SkipWhen(!File.Exists(file), "Board_Skaboto.skin.xbx not found");

        var scene = XbxSceneFile.Parse(file);

        foreach (var mat in scene.Materials)
        {
            Assert.True(mat.NumPasses > 0, $"Material 0x{mat.Checksum:X8} has no passes");
            Assert.Equal(mat.NumPasses, mat.Passes.Length);
        }
    }

    [Fact]
    public void Parse_KnownFile_MeshesHaveVertices()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(SkinDir, "Anl_Pigeon.skin.xbx");
        Assert.SkipWhen(!File.Exists(file), "Anl_Pigeon.skin.xbx not found");

        var scene = XbxSceneFile.Parse(file);

        foreach (var sector in scene.Sectors)
        {
            Assert.NotEmpty(sector.Meshes);
            foreach (var mesh in sector.Meshes)
            {
                Assert.True(mesh.Vertices.Length > 0, "Expected vertices > 0");
                Assert.True(mesh.FaceIndices.Length > 0, "Expected face indices > 0");
            }
        }
    }

    // ── Batch parse all SKIN files ──

    [Fact]
    public void BatchParse_AllSkinFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(SkinDir), "SKIN directory not found");

        var files = Directory.GetFiles(SkinDir, "*.skin.xbx");
        Assert.SkipWhen(files.Length == 0, "No .skin.xbx files found");

        var failures = new List<string>();
        var totalTris = 0;
        var totalVerts = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = XbxSceneFile.Parse(f);
                totalTris += scene.TotalTriangles;
                totalVerts += scene.TotalVertices;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} SKIN files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }

    // ── Batch parse all MDL files ──

    [Fact]
    public void BatchParse_AllMdlFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(MdlDir), "MDL directory not found");

        var files = Directory.GetFiles(MdlDir, "*.mdl.xbx");
        Assert.SkipWhen(files.Length == 0, "No .mdl.xbx files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = XbxSceneFile.Parse(f);
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} MDL files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
    }
}