using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class RwDffFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    // ── IsDffFile ──

    [Fact]
    public void IsDffFile_ValidSknFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var data = File.ReadAllBytes(file);
        Assert.True(RwDffFile.IsDffFile(data));
    }

    [Fact]
    public void IsDffFile_EmptyData_ReturnsFalse()
    {
        Assert.False(RwDffFile.IsDffFile([]));
    }

    [Fact]
    public void IsDffFile_GarbageData_ReturnsFalse()
    {
        Assert.False(RwDffFile.IsDffFile(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    // ── Parse known files ──

    [Theory]
    [InlineData("Bird_A.SKN")]
    [InlineData("Bird_B.SKN")]
    [InlineData("Crowd_A.SKN")]
    public void Parse_KnownFile_HasGeometryAndAtomics(string filename)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var clump = RwDffFile.Parse(file);

        Assert.NotEmpty(clump.Geometries);
        Assert.NotEmpty(clump.Atomics);
        Assert.NotEmpty(clump.Frames);
    }

    [Fact]
    public void Parse_BirdA_HasExpectedGeometry()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var clump = RwDffFile.Parse(file);
        var geom = clump.Geometries[0];

        Assert.True(geom.Vertices.Length > 0, "Should have vertices");
        Assert.True(geom.Triangles.Length > 0, "Should have triangles");
        Assert.NotNull(geom.UVs);
        Assert.True(geom.UVs.Length > 0, "Should have UVs");
    }

    [Fact]
    public void Parse_BirdA_HasMaterialsWithTextureNames()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var clump = RwDffFile.Parse(file);
        var geom = clump.Geometries[0];

        Assert.NotEmpty(geom.Materials);
        // At least one material should have a texture name
        Assert.Contains(geom.Materials, m => !string.IsNullOrEmpty(m.TextureName));
    }

    [Fact]
    public void Parse_BirdA_AtomicLinksValid()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var clump = RwDffFile.Parse(file);

        foreach (var atomic in clump.Atomics)
        {
            Assert.InRange(atomic.FrameIndex, 0, clump.Frames.Length - 1);
            Assert.InRange(atomic.GeometryIndex, 0, clump.Geometries.Length - 1);
        }
    }

    // ── Vertex data validation ──

    [Fact]
    public void Parse_BirdA_VerticesAreFinite()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var clump = RwDffFile.Parse(file);
        foreach (var geom in clump.Geometries)
        {
            foreach (var v in geom.Vertices)
            {
                Assert.True(float.IsFinite(v.X), "Vertex X should be finite");
                Assert.True(float.IsFinite(v.Y), "Vertex Y should be finite");
                Assert.True(float.IsFinite(v.Z), "Vertex Z should be finite");
            }
        }
    }

    // ── Batch parse all 331 SKN files ──

    [Fact]
    public void Parse_AllSknFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.SKN").ToArray();
        Assert.SkipWhen(files.Length == 0, "No SKN files found");

        var failures = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (!RwDffFile.IsDffFile(data)) continue;

                var clump = RwDffFile.Parse(data);
                Assert.NotNull(clump);
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
    public void Write_BirdA_ProducesValidGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        Assert.SkipWhen(file is null, "Bird_A.SKN not found");

        var clump = RwDffFile.Parse(file);
        var outputDir = Path.Combine(Path.GetTempPath(), "rwdff_test");
        var outputFile = Path.Combine(outputDir, "Bird_A.glb");

        try
        {
            var triangles = RwDffGltfWriter.Write(clump, outputFile);
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
    public void Write_BirdA_WithTextures_ProducesLargerGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var sknFile = paths.FindSampleFile(BuildName, "Bird_A.SKN");
        var texFile = paths.FindSampleFile(BuildName, "Bird_A.tex");
        Assert.SkipWhen(sknFile is null, "Bird_A.SKN not found");
        Assert.SkipWhen(texFile is null, "Bird_A.tex not found");

        var clump = RwDffFile.Parse(sknFile);
        var txdResult = RwTxdFile.Parse(texFile);
        Assert.True(txdResult.Success, "TEX file should parse");

        var textureProvider = RwDffGltfWriter.BuildTxdTextureProvider(txdResult);

        var outputDir = Path.Combine(Path.GetTempPath(), "rwdff_tex_test");
        var noTexFile = Path.Combine(outputDir, "no_tex.glb");
        var withTexFile = Path.Combine(outputDir, "with_tex.glb");

        try
        {
            RwDffGltfWriter.Write(clump, noTexFile);
            RwDffGltfWriter.Write(clump, withTexFile, textureProvider);

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
