using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class RwBspFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    // ── IsBspFile ──

    [Fact]
    public void IsBspFile_ValidBspFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var data = File.ReadAllBytes(file);
        Assert.True(RwBspFile.IsBspFile(data));
    }

    [Fact]
    public void IsBspFile_EmptyData_ReturnsFalse()
    {
        Assert.False(RwBspFile.IsBspFile([]));
    }

    [Fact]
    public void IsBspFile_GarbageData_ReturnsFalse()
    {
        Assert.False(RwBspFile.IsBspFile(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    // ── Parse known files ──

    [Theory]
    [InlineData("Burn.bsp")]
    [InlineData("Can.bsp")]
    [InlineData("Tok.bsp")]
    public void Parse_KnownFile_HasSectionsAndMaterials(string filename)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var world = RwBspFile.Parse(file);

        Assert.NotEmpty(world.Sections);
        Assert.NotEmpty(world.Materials);
        Assert.True(world.TotalTriangles > 0, "Should report triangles");
        Assert.True(world.TotalVertices > 0, "Should report vertices");
    }

    [Fact]
    public void Parse_Burn_HasExpectedGeometry()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var world = RwBspFile.Parse(file);

        Assert.True(world.Sections.Length > 0, "Should have sections");
        var section = world.Sections[0];
        Assert.True(section.Vertices.Length > 0, "Should have vertices");
        Assert.True(section.Triangles.Length > 0, "Should have triangles");
    }

    [Fact]
    public void Parse_Burn_HasMaterialsWithTextureNames()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var world = RwBspFile.Parse(file);

        Assert.NotEmpty(world.Materials);
        Assert.Contains(world.Materials, m => !string.IsNullOrEmpty(m.TextureName));
    }

    // ── Vertex data validation ──

    [Fact]
    public void Parse_Burn_VerticesAreFinite()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var world = RwBspFile.Parse(file);
        foreach (var section in world.Sections)
        {
            foreach (var v in section.Vertices)
            {
                Assert.True(float.IsFinite(v.X), "Vertex X should be finite");
                Assert.True(float.IsFinite(v.Y), "Vertex Y should be finite");
                Assert.True(float.IsFinite(v.Z), "Vertex Z should be finite");
            }
        }
    }

    [Fact]
    public void Parse_Burn_TriangleIndicesInRange()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var world = RwBspFile.Parse(file);
        foreach (var section in world.Sections)
        {
            foreach (var tri in section.Triangles)
            {
                Assert.InRange(tri.V0, 0, section.Vertices.Length - 1);
                Assert.InRange(tri.V1, 0, section.Vertices.Length - 1);
                Assert.InRange(tri.V2, 0, section.Vertices.Length - 1);
            }
        }
    }

    // ── Batch parse all BSP files ──

    [Fact]
    public void Parse_AllBspFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.bsp").ToArray();
        Assert.SkipWhen(files.Length == 0, "No BSP files found");

        var failures = new List<string>();
        var totalTriangles = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (!RwBspFile.IsBspFile(data)) continue;

                var world = RwBspFile.Parse(data);
                Assert.NotNull(world);
                totalTriangles += world.Sections.Sum(s => s.Triangles.Length);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} files failed:\n" +
            string.Join("\n", failures.Take(20)));
    }

    // ── glTF output ──

    [Fact]
    public void Write_Burn_ProducesValidGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Burn.bsp");
        Assert.SkipWhen(file is null, "Burn.bsp not found");

        var world = RwBspFile.Parse(file);
        var outputDir = Path.Combine(Path.GetTempPath(), "rwbsp_test");
        var outputFile = Path.Combine(outputDir, "Burn.glb");

        try
        {
            var triangles = RwBspGltfWriter.Write(world, outputFile);
            Assert.True(triangles > 0, "Should produce triangles");
            Assert.True(File.Exists(outputFile), "GLB file should exist");
            Assert.True(new FileInfo(outputFile).Length > 100, "GLB should not be empty");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Write_Burn_WithTextures_ProducesLargerGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var bspFile = paths.FindSampleFile(BuildName, "Burn.bsp");
        var texFile = paths.FindSampleFile(BuildName, "Burn.tex");
        Assert.SkipWhen(bspFile is null, "Burn.bsp not found");
        Assert.SkipWhen(texFile is null, "Burn.tex not found");

        var world = RwBspFile.Parse(bspFile);
        var txdResult = RwTxdFile.Parse(texFile);
        Assert.True(txdResult.Success, "TEX file should parse");

        var textureProvider = RwBspGltfWriter.BuildTxdTextureProvider(txdResult);

        var outputDir = Path.Combine(Path.GetTempPath(), "rwbsp_tex_test");
        var noTexFile = Path.Combine(outputDir, "no_tex.glb");
        var withTexFile = Path.Combine(outputDir, "with_tex.glb");

        try
        {
            RwBspGltfWriter.Write(world, noTexFile);
            RwBspGltfWriter.Write(world, withTexFile, textureProvider);

            var noTexSize = new FileInfo(noTexFile).Length;
            var withTexSize = new FileInfo(withTexFile).Length;

            Assert.True(withTexSize > noTexSize,
                $"With textures ({withTexSize}) should be larger than without ({noTexSize})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }
}
