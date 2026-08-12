using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class RwBspFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";

    // ── IsBspFile ──

    [CorpusFact]
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

    [Fact]
    public void Parse_MinimalWorldWithExactStruct_ReturnsEmptyWorld()
    {
        var world = RwBspFile.Parse(BuildMinimalWorld());

        Assert.Equal(0, world.TotalTriangles);
        Assert.Equal(0, world.TotalVertices);
        Assert.Empty(world.Materials);
        Assert.Empty(world.Sections);
    }

    [Fact]
    public void Parse_WorldPayloadExtendsPastFile_UsesPhysicalBoundary()
    {
        var data = BuildMinimalWorld();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 65);

        var world = RwBspFile.Parse(data);

        Assert.Empty(world.Sections);
    }

    [Fact]
    public void Parse_WorldStructSmallerThanRequired_Throws()
    {
        var data = BuildMinimalWorld();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 51);

        var exception = Assert.Throws<InvalidDataException>(() => RwBspFile.Parse(data));

        Assert.Contains("expected at least 52", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WorldStructExtendsPastWorldPayload_Throws()
    {
        var data = BuildMinimalWorld();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 53);

        var exception = Assert.Throws<InvalidDataException>(() => RwBspFile.Parse(data));

        Assert.Contains("beyond the World payload", exception.Message, StringComparison.Ordinal);
    }

    // ── Parse known files ──

    [CorpusTheory]
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

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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

    [CorpusFact]
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

    private static byte[] BuildMinimalWorld()
    {
        // WORLD payload = a 12-byte STRUCT header plus its required 52-byte body.
        var data = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x000B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 52);
        return data;
    }
}
