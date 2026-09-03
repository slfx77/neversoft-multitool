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

    [Fact]
    public void Parse_AtomicStructCannotBorrowGeometryPastItsDeclaredExtent()
    {
        var world = RwBspFile.Parse(BuildSingleAtomicWorld(structSize: 44));

        Assert.Empty(world.Sections);
    }

    [Fact]
    public void Parse_AtomicChunkCannotEscapeItsWorldExtent()
    {
        var world = RwBspFile.Parse(BuildSingleAtomicWorld(worldSize: 132));

        Assert.Empty(world.Sections);
    }

    [Fact]
    public void Parse_AtomicStructGeometryEndingExactlyAtWorldEof_IsAccepted()
    {
        var world = RwBspFile.Parse(BuildSingleAtomicWorld());

        var section = Assert.Single(world.Sections);
        Assert.Equal(new System.Numerics.Vector3(1f, 2f, 3f), Assert.Single(section.Vertices));
        var triangle = Assert.Single(section.Triangles);
        Assert.Equal((0, 0, 0, 0),
            ((int)triangle.V0, (int)triangle.V1, (int)triangle.V2, (int)triangle.MaterialIndex));
        Assert.Empty(section.TriangleCollisionFlags);
    }

    [Fact]
    public void Parse_AtomicNeversoftExtension_PreservesOrderedCollisionFlags()
    {
        var world = RwBspFile.Parse(BuildSingleAtomicWorldWithCollisionFlags(0x04C0));

        var section = Assert.Single(world.Sections);
        Assert.Equal(new ushort[] { 0x04C0 }, section.TriangleCollisionFlags);
    }

    [Fact]
    public void Parse_AtomicNeversoftExtension_WrongVersionFailsClosed()
    {
        var data = BuildSingleAtomicWorldWithCollisionFlags(0x04C0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(188, 4), 5);

        var section = Assert.Single(RwBspFile.Parse(data).Sections);

        Assert.Empty(section.TriangleCollisionFlags);
    }

    [Fact]
    public void Parse_AtomicNeversoftExtension_OpaquePluginTailIsAllowed()
    {
        var data = BuildSingleAtomicWorldWithCollisionFlags(0x04C0, opaqueTailBytes: 7);

        var section = Assert.Single(RwBspFile.Parse(data).Sections);

        Assert.Equal(new ushort[] { 0x04C0 }, section.TriangleCollisionFlags);
    }

    [Fact]
    public void Parse_AtomicNeversoftExtension_DuplicatePayloadFailsClosed()
    {
        var data = BuildSingleAtomicWorldWithCollisionFlags(0x04C0, pluginCopies: 2);

        var section = Assert.Single(RwBspFile.Parse(data).Sections);

        Assert.Empty(section.TriangleCollisionFlags);
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

    [CorpusFact]
    public void Parse_AllBspAtomicTriangles_HaveExactCollisionAvailability()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.bsp").ToArray();
        Assert.SkipWhen(files.Length == 0, "No BSP files found");

        var buildRoot = Path.Combine(paths.SampleBuildsDir!, BuildName);
        var filesWithoutFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triangleCount = 0L;
        foreach (var file in files)
        {
            var world = RwBspFile.Parse(file);
            for (var sectionIndex = 0; sectionIndex < world.Sections.Length; sectionIndex++)
            {
                var section = world.Sections[sectionIndex];
                triangleCount += section.Triangles.Length;
                if (section.Triangles.Length > 0
                    && section.TriangleCollisionFlags.Length != section.Triangles.Length)
                {
                    filesWithoutFlags.Add(
                        Path.GetRelativePath(buildRoot, file).Replace('\\', '/'));
                }
            }
        }

        // The three DCC/source BSPs predate the runtime Neversoft collision
        // payload. They remain ordinary renderable BSPs, while collision mode
        // deliberately declines them. Every shipped/prebuilt world, including
        // every main and sky level BSP, has a complete one-u16-per-triangle join.
        Assert.Equal(43, files.Length);
        Assert.True(triangleCount > 0);
        Assert.Equal(
            [
                "SKATE3/Intermediate/Models/SkWare_RW.bsp",
                "SKATE3/Intermediate/Models/SkWare_RW_2.bsp",
                "SKATE3/Source/Ware/Ware.bsp"
            ],
            filesWithoutFlags.Order(StringComparer.OrdinalIgnoreCase));
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

    private static byte[] BuildSingleAtomicWorld(
        uint worldSize = 152,
        uint atomicSize = 76,
        uint structSize = 64)
    {
        // The physical bytes always contain the vertex and triangle at 144..163.
        // Individual tests independently shorten the nested STRUCT or its WORLD
        // parent; the exact-EOF control owns every byte through offset 164.
        var data = new byte[164];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x000B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), worldSize);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 52);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(52, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(56, 4), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76, 4), 0x0009);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80, 4), atomicSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(88, 4), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92, 4), structSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(104, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(108, 4), 1);

        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(144, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(148, 4), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(152, 4), 3f);
        // Triangle at 156 is all zero: material 0 and three references to vertex 0.
        return data;
    }

    private static byte[] BuildSingleAtomicWorldWithCollisionFlags(
        ushort flags,
        int opaqueTailBytes = 0,
        int pluginCopies = 1)
    {
        // WORLD = STRUCT(64 bytes total) + ATOMIC. ATOMIC starts with a
        // 76-byte STRUCT and one EXTENSION containing one or more plugins.
        // Each 0x0294AF01 plugin is version 6 + one u16 plus an optional opaque
        // tail, matching the larger payloads carried by the real BSP sectors.
        var pluginPayloadSize = sizeof(uint) + sizeof(ushort) + opaqueTailBytes;
        var pluginTotalSize = 12 + pluginPayloadSize;
        var extensionPayloadSize = pluginCopies * pluginTotalSize;
        var data = new byte[176 + extensionPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x000B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)(data.Length - 12));

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 52);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(52, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(56, 4), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76, 4), 0x0009);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80, 4), (uint)(data.Length - 88));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(88, 4), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92, 4), 64);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(104, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(108, 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(144, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(148, 4), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(152, 4), 3f);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(164, 4), 0x0003);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(168, 4), (uint)extensionPayloadSize);
        for (var copy = 0; copy < pluginCopies; copy++)
        {
            var pluginOffset = 176 + copy * pluginTotalSize;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pluginOffset, 4), 0x0294AF01);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pluginOffset + 4, 4),
                (uint)pluginPayloadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pluginOffset + 12, 4), 6);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pluginOffset + 16, 2), flags);
            data.AsSpan(pluginOffset + 18, opaqueTailBytes).Fill(0xA5);
        }
        return data;
    }
}
