using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Collision;

namespace NeversoftMultitool.Tests.Core.Formats.Collision;

public sealed class NgcColFileTests(TestPaths paths)
{
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    // ── Format detection ──

    [Fact]
    public void IsNgcColFile_LittleEndianColHeader_ReturnsFalse()
    {
        var data = new byte[64];
        BitConverter.GetBytes(10).CopyTo(data, 0); // LE version 10
        Assert.False(NgcColFile.IsNgcColFile(data));
    }

    [Fact]
    public void IsNgcColFile_TooSmall_ReturnsFalse()
    {
        Assert.False(NgcColFile.IsNgcColFile(new byte[40]));
    }

    [Fact]
    public void IsNgcColFile_MinimalSynthetic_ReturnsTrue()
    {
        Assert.True(NgcColFile.IsNgcColFile(BuildMinimalFile()));
    }

    // ── Synthetic round trip + strictness ──

    [Fact]
    public void Parse_MinimalSynthetic_ReadsEveryRegion()
    {
        var scene = NgcColFile.Parse(BuildMinimalFile());

        Assert.Equal(10, scene.Version);
        Assert.Equal(1, scene.SuperSectorRows);
        Assert.Equal(1, scene.SuperSectorCols);
        var obj = Assert.Single(scene.Objects);
        Assert.Equal(0x12345678u, obj.Checksum);
        Assert.Equal(3, obj.NumVerts);
        var face = Assert.Single(obj.Faces);
        Assert.Equal(new NgcColFace(0, 0, 0, 1, 2), face);
        Assert.True(obj.BspRoot.IsLeaf);
        Assert.Equal([0], obj.BspRoot.LeafFaceIndices!);
        Assert.True(scene.CornerIntensitiesUniform);
        Assert.True(scene.FaceIndicesObjectContained);
        Assert.Equal(1, scene.PoolElementCount);
    }

    [Fact]
    public void Parse_FaceVertexIndexOutsideFile_Throws()
    {
        var data = BuildMinimalFile();
        // face i2 at faceStart + 8 (faceStart = 124)
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(124 + 8), 3);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_NonZeroVertexPoolSlot_Throws()
    {
        var data = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(56 + 48), 1);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_NodeSizeNotMultipleOfEight_Throws()
    {
        var data = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(136), 4);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_LeafListPastPoolEnd_Throws()
    {
        var data = BuildMinimalFile();
        // leaf numFaces at nodeBase (140)
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(140), 2);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_PoolFaceIndexOutsideObject_Throws()
    {
        var data = BuildMinimalFile();
        // sole pool element at 148 — object has 1 face, so index 1 is invalid
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(148), 1);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    // ── Real fixture: loose secret_tape (2 objects, leaf-only BSP) ──

    [Fact]
    public void Parse_SecretTape_PinsStructure()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawGcBuild, "secret_tape.col.ngc");
        Assert.SkipWhen(file is null, "secret_tape.col.ngc not found");

        var scene = NgcColFile.Parse(file);
        Assert.Equal(10, scene.Version);
        Assert.Equal(12, scene.TotalVerts);
        Assert.Equal(16, scene.TotalFaces);
        Assert.Equal(16, scene.PoolElementCount);
        Assert.True(scene.CornerIntensitiesUniform);
        Assert.True(scene.FaceIndicesObjectContained);

        Assert.Equal(2, scene.Objects.Length);
        Assert.Equal(0x6D10C4BBu, scene.Objects[0].Checksum);
        Assert.Equal(0x380A9488u, scene.Objects[1].Checksum);
        Assert.Equal(8, scene.Objects[0].NumVerts);
        Assert.Equal(14, scene.Objects[0].Faces.Length);
        Assert.Equal(4, scene.Objects[1].NumVerts);
        Assert.Equal(2, scene.Objects[1].Faces.Length);
        Assert.Equal(8, scene.Objects[1].FirstVertIndex);
        Assert.Equal(14, scene.Objects[1].FirstFaceIndex);

        // Both trees are single leaves listing every face object-relative.
        Assert.True(scene.Objects[0].BspRoot.IsLeaf);
        Assert.Equal(14, scene.Objects[0].BspRoot.LeafFaceIndices!.Length);
        Assert.True(scene.Objects[1].BspRoot.IsLeaf);
        Assert.Equal([0, 1], scene.Objects[1].BspRoot.LeafFaceIndices!);

        // Object 1's faces index the global vertex numbering (8..11).
        Assert.All(scene.Objects[1].Faces, static face =>
        {
            Assert.InRange(face.V0, 8, 11);
            Assert.InRange(face.V1, 8, 11);
            Assert.InRange(face.V2, 8, 11);
        });
    }

    [Fact]
    public void Serialize_SecretTape_ProducesSchemaManifest()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawGcBuild, "secret_tape.col.ngc");
        Assert.SkipWhen(file is null, "secret_tape.col.ngc not found");

        var json = NgcColJsonExporter.Serialize(file, NgcColFile.Parse(file));
        Assert.Contains("\"schema\": \"neversoft.ngc.col\"", json);
        Assert.Contains("\"formatVersion\": 10", json);
        Assert.Contains("\"checksum\": \"0x6D10C4BB\"", json);
        // Uniform corner intensities stay summarized, not dumped.
        Assert.DoesNotContain("cornerIntensitiesHex", json);
    }

    [Fact]
    public void GetOutputPath_MapsColNgcToColJson()
    {
        var result = NgcColCommand.GetOutputPath(
            "input.col.ngc", "input.col.ngc", "TestOutput");
        Assert.Equal(Path.Combine("TestOutput", "input.col.json"), result);
    }

    // ── Whole-corpus sweep ──

    [CorpusFact]
    public void Parse_ThawGcCorpus_EveryFileParsesWithPinnedTotals()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(ThawGcBuild, "*.col.ngc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .col.ngc files found");

        var fileCount = 0;
        var objects = 0;
        var verts = 0L;
        var faces = 0L;
        var leaves = 0;
        var interior = 0;
        var uniform = 0;
        var varied = 0;
        var contained = 0;
        var compact = 0;

        foreach (var file in files)
        {
            var scene = NgcColFile.Parse(file);
            fileCount++;
            objects += scene.Objects.Length;
            verts += scene.TotalVerts;
            faces += scene.TotalFaces;
            foreach (var obj in scene.Objects)
            {
                var total = obj.BspRoot.CountNodes();
                var leafCount = obj.BspRoot.CountLeaves();
                leaves += leafCount;
                interior += total - leafCount;
            }

            if (scene.CornerIntensitiesUniform) uniform++;
            else varied++;
            if (scene.FaceIndicesObjectContained) contained++;
            else compact++;
        }

        Assert.Equal(1402, fileCount);
        Assert.Equal(18431, objects);
        Assert.Equal(963_971, verts);
        Assert.Equal(977_186, faces);
        Assert.Equal(129_199, leaves);
        Assert.Equal(110_768, interior);
        Assert.Equal(1151, uniform);
        Assert.Equal(251, varied);
        Assert.Equal(1258, contained);
        Assert.Equal(144, compact);
    }

    // ── Synthetic minimal file ──
    // 1 object, 3 verts, 1 face, single-leaf BSP, 1 pool element:
    //   0   header (24B)
    //  24   scene bounds (32B)
    //  56   object record (64B)
    // 120   corner intensities (3B) + 1 align pad
    // 124   face (10B)
    // 134   odd-face-count pad (2B)
    // 136   node array size u32 = 8
    // 140   leaf node (8B)
    // 148   pool: one u16
    private static byte[] BuildMinimalFile()
    {
        var data = new byte[150];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 10);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);   // objects
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 3);   // verts
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], 1);  // faces
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], 1);  // ss rows
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], 1);  // ss cols

        BinaryPrimitives.WriteUInt32BigEndian(span[56..], 0x12345678); // checksum
        BinaryPrimitives.WriteUInt32BigEndian(span[60..], 3);  // numVerts
        BinaryPrimitives.WriteUInt16BigEndian(span[64..], 1);  // numFaces

        span[120] = 0xFF;
        span[121] = 0xFF;
        span[122] = 0xFF;

        // face: flags 0, terrain 0, verts (0,1,2)
        BinaryPrimitives.WriteUInt16BigEndian(span[130..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[132..], 2);

        BinaryPrimitives.WriteUInt32BigEndian(span[136..], 8); // node array size
        BinaryPrimitives.WriteUInt16BigEndian(span[140..], 1); // leaf face count
        span[143] = 3;                                          // leaf axis
        // leaf pool offset (u32 at 144) = 0; pool element (u16 at 148) = 0
        return data;
    }
}
