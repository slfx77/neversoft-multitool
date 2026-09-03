using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SharpGLTF.Schema2;
using System.Buffers.Binary;

namespace NeversoftMultitool.Tests.Core.Formats.Collision;

public sealed class ColFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string Thps4Ps2BuildName = "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";

    // ── Format Detection ──

    [CorpusFact]
    public void IsColFile_ValidColFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_EmptyData_ReturnsFalse()
    {
        Assert.False(ColFile.IsColFile([]));
    }

    [Fact]
    public void IsColFile_TooSmall_ReturnsFalse()
    {
        Assert.False(ColFile.IsColFile(new byte[16]));
    }

    [Fact]
    public void IsColFile_WrongVersion_ReturnsFalse()
    {
        var data = new byte[32];
        BitConverter.GetBytes(99).CopyTo(data, 0); // version 99
        Assert.False(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_Version9_ReturnsTrue()
    {
        var data = new byte[32];
        BitConverter.GetBytes(9).CopyTo(data, 0);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_Version8_ReturnsTrue()
    {
        var data = new byte[32];
        BitConverter.GetBytes(8).CopyTo(data, 0);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_Version10_ReturnsTrue()
    {
        var data = new byte[32];
        BitConverter.GetBytes(10).CopyTo(data, 0);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void Parse_DeclaredFloatVertexPastEndOfFile_Throws()
    {
        var data = CreateOneObjectFile(totalVertices: 1, totalLargeVertices: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("vertex data is truncated", error.Message);
    }

    [Fact]
    public void Parse_DeclaredSmallFacePastEndOfFile_Throws()
    {
        var data = CreateOneObjectFile(totalVertices: 3, totalLargeVertices: 3, length: 136);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(40, 2), 1);
        data[42] = 1;

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("face data is truncated", error.Message);
    }

    [Fact]
    public void Parse_EmptyHeaderWithNegativeTotalVertexCount_Throws()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("total vertex count is negative: -1", error.Message);
    }

    [Theory]
    [InlineData(12, "total large-face")]
    [InlineData(16, "total small-face")]
    [InlineData(20, "total large-vertex")]
    [InlineData(24, "total small-vertex")]
    public void Parse_NegativeAggregateCount_Throws(int fieldOffset, string fieldName)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(fieldOffset), -1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains($"{fieldName} count is negative: -1", error.Message);
    }

    [Fact]
    public void Parse_EmptyHeaderWithZeroAggregateCounts_IsAccepted()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);

        var scene = ColFile.Parse(data);

        Assert.Equal(10, scene.Version);
        Assert.Empty(scene.Objects);
    }

    [Fact]
    public void Parse_Version8_UsesOrdinalVerticesInlineRgbaAndTwelveByteLargeFaces()
    {
        var scene = ColFile.Parse(CreateThps4File());

        Assert.Equal(8, scene.Version);
        Assert.Equal(2, scene.Objects.Length);
        Assert.Equal(6, scene.TotalVertices);
        Assert.Equal(3, scene.TotalTriangles);

        var large = scene.Objects[0];
        Assert.Equal(new System.Numerics.Vector3(10, 11, 12), large.Vertices[1]);
        Assert.Equal(new ColFace(0x1234, 0x5678, 0, 1, 2), large.Faces[0]);
        Assert.Equal(new ColFace(0x4321, 0x8765, 2, 1, 0), large.Faces[1]);
        Assert.Equal([10, 20, 30, 40], large.VertexColorsRgba[..4]);
        Assert.Equal(20, large.Intensities[0]);

        var small = scene.Objects[1];
        Assert.Equal(new System.Numerics.Vector3(30, 31, 32), small.Vertices[0]);
        Assert.Equal(new ColFace(0x1111, 0x2222, 2, 0, 1), small.Faces[0]);
        Assert.Equal([40, 50, 60, 70], small.VertexColorsRgba[..4]);
    }

    [Fact]
    public void Parse_Version8_RejectsNonFiniteVertexAndBoundingBox()
    {
        var vertex = CreateThps4File();
        BinaryPrimitives.WriteInt32LittleEndian(vertex.AsSpan(160, 4), 0x7FC00000);
        var vertexError = Assert.Throws<InvalidDataException>(() => ColFile.Parse(vertex));
        Assert.Contains("vertex 0 is not finite", vertexError.Message);

        var bbox = CreateThps4File();
        BinaryPrimitives.WriteInt32LittleEndian(bbox.AsSpan(48, 4), 0x7F800000);
        var bboxError = Assert.Throws<InvalidDataException>(() => ColFile.Parse(bbox));
        Assert.Contains("bounding-box minimum is not finite", bboxError.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsOutOfRangeFaceIndex()
    {
        var data = CreateThps4File();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(260, 2), 3);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("indices (3, 1, 2) outside [0, 3)", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsTruncatedAggregateFaceRegion()
    {
        const int faceRegionEnd = 256 + 2 * 12 + 8;
        var data = CreateThps4File()[..(faceRegionEnd - 5)];

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("face data is truncated", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsMissingOrTruncatedBspTail()
    {
        const int faceRegionEnd = 256 + 2 * 12 + 8;
        var complete = CreateThps4File();

        var missingSize = Assert.Throws<InvalidDataException>(
            () => ColFile.Parse(complete[..faceRegionEnd]));
        Assert.Contains("BSP-size data is truncated", missingSize.Message);

        const int nodeBase = faceRegionEnd + sizeof(uint);
        var truncatedNodes = Assert.Throws<InvalidDataException>(
            () => ColFile.Parse(complete[..(nodeBase + 75)]));
        Assert.Contains("BSP-node data is truncated", truncatedNodes.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Parse_Version8_RejectsBytesOutsideTheDeclaredBspTail(int trailingBytes)
    {
        var complete = CreateThps4File();
        var extended = new byte[complete.Length + trailingBytes];
        complete.CopyTo(extended, 0);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(extended));

        Assert.Contains("BSP face-index", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsCyclicBspNodesAndOutOfRangeLeafFaces()
    {
        const int faceRegionEnd = 256 + 2 * 12 + 8;
        const int nodeBase = faceRegionEnd + sizeof(uint);
        const int faceIndexBase = nodeBase + 76;

        var cycle = CreateThps4File();
        BinaryPrimitives.WriteUInt32LittleEndian(cycle.AsSpan(nodeBase + 8), 0);
        var cycleError = Assert.Throws<InvalidDataException>(() => ColFile.Parse(cycle));
        Assert.Contains("cyclic", cycleError.Message);

        var badFace = CreateThps4File();
        BinaryPrimitives.WriteUInt16LittleEndian(badFace.AsSpan(faceIndexBase), 2);
        var faceError = Assert.Throws<InvalidDataException>(() => ColFile.Parse(badFace));
        Assert.Contains("BSP face index 2", faceError.Message);
        Assert.Contains("outside [0, 2)", faceError.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsNonzeroBspLeafPad()
    {
        const int faceRegionEnd = 256 + 2 * 12 + 8;
        const int nodeBase = faceRegionEnd + sizeof(uint);
        const int firstLeafOffset = nodeBase + 16;
        var data = CreateThps4File();
        data[firstLeafOffset + 1] = 1;

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("non-zero pad byte 1", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsBspThatOmitsAnObjectFace()
    {
        const int faceRegionEnd = 256 + 2 * 12 + 8;
        const int nodeBase = faceRegionEnd + sizeof(uint);
        const int faceIndexBase = nodeBase + 76;
        var data = CreateThps4File();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceIndexBase + sizeof(ushort)), 0);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("object 0 BSP does not reference face 1", error.Message);
        Assert.Contains("1/2 faces are reachable", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsAggregateCountMismatch()
    {
        var data = CreateThps4File();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 2);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("object vertex count 5 does not match aggregate count 6", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsOverlappingOrdinalVertexRanges()
    {
        var data = CreateThps4File();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(144, 4), 2);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("vertex ordinal range", error.Message);
        Assert.Contains("overlaps", error.Message);
    }

    [Fact]
    public void Parse_Version8_RejectsFixedVertexProfile()
    {
        var data = CreateThps4File();
        data[43] = 1;

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("fixed vertices", error.Message);
    }

    private static byte[] CreateOneObjectFile(
        int totalVertices,
        int totalLargeVertices,
        int length = 96)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), totalVertices);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), totalLargeVertices);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88, 4), -1);
        return data;
    }

    private static byte[] CreateThps4File()
    {
        const int baseVert = 160; // Align16(32 + 2 * 64)
        const int baseFace = baseVert + 6 * 16;
        const int faceBytes = 2 * 12 + 8;
        const int nodeBytes = 76; // one internal node and three leaves
        const int nodeBase = baseFace + faceBytes + sizeof(uint);
        const int faceIndexBase = nodeBase + nodeBytes;
        var data = new byte[faceIndexBase + 3 * sizeof(ushort)];
        BinaryPrimitives.WriteInt32LittleEndian(data, 8);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 6);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1);

        WriteObjectHeader(data, 32, 0x11111111, 3, 2, false, 0, 0, 0);
        WriteObjectHeader(data, 96, 0x22222222, 3, 1, true, 3, 24, 56);

        for (var i = 0; i < 6; i++)
        {
            var offset = baseVert + i * 16;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), i * 10);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 4), i * 10 + 1);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 8), i * 10 + 2);
            data[offset + 12] = (byte)(10 + i * 10);
            data[offset + 13] = (byte)(20 + i * 10);
            data[offset + 14] = (byte)(30 + i * 10);
            data[offset + 15] = (byte)(40 + i * 10);
        }

        WriteLargeFace(data, baseFace, 0x1234, 0x5678, 0, 1, 2);
        WriteLargeFace(data, baseFace + 12, 0x4321, 0x8765, 2, 1, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(baseFace + 24), 0x1111);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(baseFace + 26), 0x2222);
        data[baseFace + 28] = 2;
        data[baseFace + 29] = 0;
        data[baseFace + 30] = 1;

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(baseFace + faceBytes), nodeBytes);
        WriteBspNode(data, nodeBase, axis: 0, split: 25, left: 16, right: 36);
        WriteBspLeaf(data, nodeBase + 16, faceCount: 1, firstFaceIndex: 0);
        WriteBspLeaf(data, nodeBase + 36, faceCount: 1, firstFaceIndex: 1);
        WriteBspLeaf(data, nodeBase + 56, faceCount: 1, firstFaceIndex: 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceIndexBase), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceIndexBase + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceIndexBase + 4), 0);
        return data;

        static void WriteObjectHeader(
            Span<byte> bytes,
            int offset,
            uint checksum,
            ushort vertices,
            ushort faces,
            bool smallFaces,
            uint firstVertex,
            uint firstFace,
            uint bspRoot)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], checksum);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 6)..], vertices);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 8)..], faces);
            bytes[offset + 10] = smallFaces ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 12)..], firstFace);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 16)..], -100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 20)..], -100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 24)..], -100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 28)..], 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 32)..], 100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 36)..], 100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 40)..], 100);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 44)..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 48)..], firstVertex);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 52)..], bspRoot);
        }

        static void WriteBspNode(
            Span<byte> bytes,
            int offset,
            uint axis,
            float split,
            uint left,
            uint right)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], axis);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[(offset + 4)..], split);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 8)..], left);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 12)..], right);
        }

        static void WriteBspLeaf(
            Span<byte> bytes,
            int offset,
            ushort faceCount,
            uint firstFaceIndex)
        {
            bytes[offset] = byte.MaxValue;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 2)..], faceCount);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 8)..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 12)..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset + 16)..], firstFaceIndex);
        }

        static void WriteLargeFace(
            Span<byte> bytes,
            int offset,
            ushort flags,
            ushort terrain,
            ushort v0,
            ushort v1,
            ushort v2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 2)..], terrain);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 4)..], v0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 6)..], v1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(offset + 8)..], v2);
        }
    }

    // ── Parsing Known Files ──

    [CorpusFact]
    public void Parse_Arrow_HasExpectedStructure()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        Assert.Equal(10, scene.Version);
        Assert.Single(scene.Objects);
        Assert.Equal(30, scene.TotalVertices);
        Assert.Equal(26, scene.TotalTriangles);
    }

    [CorpusTheory]
    [InlineData("Arrow.col.xbx")]
    [InlineData("Anl_Cat.col.xbx")]
    public void Parse_KnownFile_HasObjectsAndFaces(string filename)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var scene = ColFile.Parse(file);
        Assert.True(scene.Objects.Length > 0);
        Assert.True(scene.TotalTriangles > 0);
    }

    // ── Vertex Validation ──

    [CorpusFact]
    public void Parse_Arrow_VerticesAreFinite()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        foreach (var obj in scene.Objects)
        {
            foreach (var v in obj.Vertices)
            {
                Assert.True(float.IsFinite(v.X), $"Vertex X should be finite, got {v.X}");
                Assert.True(float.IsFinite(v.Y), $"Vertex Y should be finite, got {v.Y}");
                Assert.True(float.IsFinite(v.Z), $"Vertex Z should be finite, got {v.Z}");
            }
        }
    }

    [CorpusFact]
    public void Parse_Arrow_FaceIndicesInRange()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        foreach (var obj in scene.Objects)
        {
            foreach (var face in obj.Faces)
            {
                Assert.True(face.V0 >= 0 && face.V0 < obj.Vertices.Length,
                    $"V0 index {face.V0} out of range [0, {obj.Vertices.Length})");
                Assert.True(face.V1 >= 0 && face.V1 < obj.Vertices.Length,
                    $"V1 index {face.V1} out of range [0, {obj.Vertices.Length})");
                Assert.True(face.V2 >= 0 && face.V2 < obj.Vertices.Length,
                    $"V2 index {face.V2} out of range [0, {obj.Vertices.Length})");
            }
        }
    }

    // ── Batch Parsing ──

    [CorpusFact]
    public void Parse_AllColFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.col.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No COL files found");

        var failures = new List<string>();
        var totalFiles = 0;
        var totalTriangles = 0;

        foreach (var file in files)
        {
            totalFiles++;
            try
            {
                var data = File.ReadAllBytes(file);
                if (!ColFile.IsColFile(data)) continue;
                var scene = ColFile.Parse(data);
                Assert.NotNull(scene);
                totalTriangles += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{totalFiles} files failed:\n" +
            string.Join("\n", failures.Take(20)));

        // Sanity check: we expect ~957 files and >1M triangles
        Assert.True(totalFiles > 900, $"Expected >900 COL files, found {totalFiles}");
        Assert.True(totalTriangles > 1_000_000, $"Expected >1M triangles, found {totalTriangles}");
    }

    [CorpusFact]
    public void Parse_AllLittleEndianColFiles_PinsTheWholeSupportedCorpus()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = Directory.EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
            .Where(IsLittleEndianColName)
            .ToArray();
        Assert.Equal(11_266, files.Length);

        var failures = new List<string>();
        var rejected = new List<(string Path, int Version)>();
        var accepted = 0;
        var version8Files = 0;
        long totalObjects = 0;
        long totalTriangles = 0;
        long version8Objects = 0;
        long version8Vertices = 0;
        long version8Triangles = 0;

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var version = data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : int.MinValue;
            if (!ColFile.IsColFile(data))
            {
                rejected.Add((file, version));
                continue;
            }

            try
            {
                var scene = ColFile.Parse(data);
                accepted++;
                totalObjects += scene.Objects.Length;
                totalTriangles += scene.TotalTriangles;
                if (version == 8)
                {
                    version8Files++;
                    version8Objects += scene.Objects.Length;
                    version8Vertices += scene.TotalVertices;
                    version8Triangles += scene.TotalTriangles;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{file}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} supported-version COL files failed:\n" +
            string.Join("\n", failures.Take(20)));
        Assert.Equal(11_265, accepted);
        Assert.Equal(1_007, version8Files);
        Assert.Equal(385_448, totalObjects);
        Assert.Equal(15_490_432, totalTriangles);
        Assert.Equal(12_523, version8Objects);
        Assert.Equal(699_488, version8Vertices);
        Assert.Equal(748_848, version8Triangles);

        var unsupported = Assert.Single(rejected);
        Assert.Equal(1, unsupported.Version);
        Assert.Equal("canada.col.ps2", Path.GetFileName(unsupported.Path), ignoreCase: true);
        Assert.Contains(Thps4Ps2BuildName, unsupported.Path, StringComparison.OrdinalIgnoreCase);
    }

    // ── glTF Output ──

    [CorpusFact]
    public void Write_Arrow_ProducesValidGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        var outputDir = Path.Combine(Path.GetTempPath(), "col_test_" + Guid.NewGuid().ToString("N")[..8]);
        var outputFile = Path.Combine(outputDir, "Arrow.glb");

        try
        {
            var triangles = ColGltfWriter.Write(scene, outputFile);
            Assert.Equal(26, triangles);
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
    public void Write_Thps4Chicken_ProducesLoadableGlbWithEveryTriangle()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(Thps4Ps2BuildName, "Anl_Chicken.col.ps2");
        Assert.SkipWhen(file is null, "Anl_Chicken.col.ps2 not found");

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(file),
            FileName = Path.GetFileName(file),
            OutputStem = "Anl_Chicken",
            SourceKind = ModelSourceKind.Collision
        });
        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(168, document.TriangleCount);
        Assert.Equal(168, triangles);
        Assert.NotNull(glbBytes);
        var model = ModelRoot.ReadGLB(new MemoryStream(glbBytes));
        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        Assert.Equal(168 * 3, primitive.IndexAccessor!.Count);
        Assert.NotNull(primitive.GetVertexAccessor("COLOR_0"));
    }

    [Fact]
    public void Write_EmptyScene_ProducesNoFile()
    {
        var scene = new ColScene
        {
            Version = 10,
            Objects = []
        };

        var outputDir = Path.Combine(Path.GetTempPath(), "col_test_" + Guid.NewGuid().ToString("N")[..8]);
        var outputFile = Path.Combine(outputDir, "empty.glb");

        try
        {
            var triangles = ColGltfWriter.Write(scene, outputFile);
            Assert.Equal(0, triangles);
            Assert.False(File.Exists(outputFile), "Empty scene should not produce a file");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    // ── Batch glTF Output ──

    [CorpusFact]
    public void Write_AllColFiles_ZeroGlbFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.col.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No COL files found");

        var outputDir = Path.Combine(Path.GetTempPath(), "col_batch_" + Guid.NewGuid().ToString("N")[..8]);
        var failures = new List<string>();
        var converted = 0;

        try
        {
            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    if (!ColFile.IsColFile(data)) continue;

                    var scene = ColFile.Parse(data);
                    var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
                    var outputFile = Path.Combine(outputDir, stem + ".glb");
                    ColGltfWriter.Write(scene, outputFile);
                    converted++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                $"{failures.Count}/{files.Length} files failed glTF conversion:\n" +
                string.Join("\n", failures.Take(20)));
            Assert.True(converted > 900, $"Expected >900 conversions, got {converted}");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static bool IsLittleEndianColName(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".col", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".col.ps2", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".col.wpc", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".col.xbx", StringComparison.OrdinalIgnoreCase);
    }
}
