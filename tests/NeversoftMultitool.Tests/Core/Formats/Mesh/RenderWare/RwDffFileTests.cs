using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;

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

    // ── LOD UV fidelity (2026-07-16 LOD 2/3 texture-mapping audit) ──
    //
    // The LOD 2/3 "texture mapping off" report was root-caused to the SHIPPED file
    // data (sloppy LOD UV authoring), not the decoder: the raw UV array, the binmesh
    // strips, normals, and skin weights all cross-validate the current decode
    // (tools/diagnostics/rwdff_lod_uv_probe.py). These tests pin the exact UV↔vertex
    // pairing on two LOD fixtures so any future change to the geometry-struct walk
    // (surface-property skip, PRELIT block, UV-set count) fails loudly instead of
    // silently re-pairing UVs.

    [Theory]
    // pedestrian_a_LOD02.skn: 190 verts; first / middle / last vertex.
    [InlineData("pedestrian_a_LOD02.skn", 190, 0, 7.593017578125f, -6.6396074295043945f, -0.7926748991012573f,
        0.8252797722816467f, 0.4846044182777405f)]
    [InlineData("pedestrian_a_LOD02.skn", 190, 95, 8.909504890441895f, 2.8484222888946533f, -2.5893735885620117f,
        0.21281440556049347f, 0.2966340184211731f)]
    [InlineData("pedestrian_a_LOD02.skn", 190, 189, 2.599452018737793f, -4.124118804931641f, 32.48052215576172f,
        0.30156517028808594f, 0.5579977035522461f)]
    // ped_canada_a_LOD03.skn: 199 verts; note the authored +1 U-tile offset (wraps under REPEAT).
    [InlineData("ped_canada_a_LOD03.skn", 199, 0, -1.6966094970703125f, -3.9007437229156494f, 24.144573211669922f,
        1.3206835985183716f, 0.8163937926292419f)]
    [InlineData("ped_canada_a_LOD03.skn", 199, 99, -10.073600769042969f, 3.7604992389678955f, 8.866317749023438f,
        1.314725637435913f, 0.6667559146881104f)]
    [InlineData("ped_canada_a_LOD03.skn", 199, 198, 4.384754657745361f, 4.607649326324463f, -39.62012481689453f,
        1.0068659782409668f, 0.4907910227775574f)]
    public void Parse_LodFixture_UvVertexPairingIsBitExact(
        string filename, int vertexCount, int index,
        float px, float py, float pz, float u, float v)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var clump = RwDffFile.Parse(file);
        var geom = clump.Geometries[0];

        Assert.Equal(vertexCount, geom.Vertices.Length);
        Assert.NotNull(geom.UVs);
        Assert.Equal(vertexCount, geom.UVs.Length);

        Assert.Equal(px, geom.Vertices[index].X);
        Assert.Equal(py, geom.Vertices[index].Y);
        Assert.Equal(pz, geom.Vertices[index].Z);
        Assert.Equal(u, geom.UVs[index].X);
        Assert.Equal(v, geom.UVs[index].Y);
    }

    [Theory]
    [InlineData("pedestrian_a.skn")]
    [InlineData("pedestrian_a_LOD01.skn")]
    [InlineData("pedestrian_a_LOD02.skn")]
    [InlineData("pedestrian_a_LOD03.skn")]
    public void Parse_PedestrianFamily_UvLayoutIsSane(string filename)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var clump = RwDffFile.Parse(file);
        var geom = clump.Geometries[0];

        // One UV per vertex, all finite and within the wrap-tile range these
        // characters author into ([0,1] plus whole-tile offsets, never garbage).
        Assert.NotNull(geom.UVs);
        Assert.Equal(geom.Vertices.Length, geom.UVs.Length);
        foreach (var uv in geom.UVs)
        {
            Assert.True(float.IsFinite(uv.X) && float.IsFinite(uv.Y), "UV should be finite");
            Assert.InRange(uv.X, -8f, 8f);
            Assert.InRange(uv.Y, -8f, 8f);
        }

        // Every triangle's material id must resolve inside the material list.
        Assert.NotEmpty(geom.Materials);
        foreach (var tri in geom.Triangles)
        {
            Assert.InRange(tri.MaterialIndex, 0, geom.Materials.Length - 1);
            Assert.InRange(tri.V0, 0, geom.Vertices.Length - 1);
            Assert.InRange(tri.V1, 0, geom.Vertices.Length - 1);
            Assert.InRange(tri.V2, 0, geom.Vertices.Length - 1);
        }
    }

    // ── Batch parse all 331 SKN files ──

    [CorpusFact]
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