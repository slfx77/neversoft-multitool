using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Collision;

public sealed class XenColFileTests(TestPaths paths)
{
    private const string XenBuild = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";
    private const string PcBuild = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";

    [Fact]
    public void Parse_BigEndianV10_DecodesTypedVertexPoolsIntensitiesAndFaces()
    {
        var data = CreateXenFile();

        Assert.True(ColFile.IsColFile(data));
        var scene = ColFile.Parse(data);

        Assert.Equal(10, scene.Version);
        Assert.Equal(2, scene.Objects.Length);
        Assert.Equal(6, scene.TotalVertices);
        Assert.Equal(2, scene.TotalTriangles);

        var fixedObject = scene.Objects[0];
        Assert.Equal(0x9B2C8826u, fixedObject.Checksum);
        Assert.Equal(new System.Numerics.Vector3(101, 202, 303), fixedObject.Vertices[0]);
        Assert.Equal([11, 22, 33], fixedObject.Intensities);
        Assert.Equal(new ColFace(0x1234, 0x5678, 2, 0, 1), fixedObject.Faces[0]);

        var floatObject = scene.Objects[1];
        Assert.Equal(new System.Numerics.Vector3(10, 11, 12), floatObject.Vertices[1]);
        Assert.Equal([44, 55, 66], floatObject.Intensities);
        Assert.Equal(new ColFace(0x4321, 0x8765, 0, 1, 2), floatObject.Faces[0]);
    }

    [Fact]
    public void Parse_BigEndianV10_RejectsMalformedBoundsCountsFiniteValuesAndIndices()
    {
        var truncated = CreateXenFile()[..^5];
        Assert.Contains(
            "face data is truncated",
            Assert.Throws<InvalidDataException>(() => ColFile.Parse(truncated)).Message);

        var countMismatch = CreateXenFile();
        BinaryPrimitives.WriteUInt16BigEndian(countMismatch.AsSpan(80 + 64 + 6), 2);
        Assert.Contains(
            "object vertex count 5 does not match aggregate count 6",
            Assert.Throws<InvalidDataException>(() => ColFile.Parse(countMismatch)).Message);

        var nonFinite = CreateXenFile();
        BinaryPrimitives.WriteInt32BigEndian(nonFinite.AsSpan(208), 0x7FC00000);
        Assert.Contains(
            "vertex 0 is not finite",
            Assert.Throws<InvalidDataException>(() => ColFile.Parse(nonFinite)).Message);

        var badIndex = CreateXenFile();
        badIndex[268 + 4] = 3;
        Assert.Contains(
            "indices (3, 0, 1) outside [0, 3)",
            Assert.Throws<InvalidDataException>(() => ColFile.Parse(badIndex)).Message);
    }

    [CorpusFact]
    public void Parse_AllXenColFiles_ZeroFailuresAndExactCorpusTotals()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(XenBuild, "*.col.xen").ToArray();
        Assert.Equal(764, files.Length);

        var failures = new List<string>();
        long objects = 0;
        long vertices = 0;
        long triangles = 0;
        foreach (var file in files)
        {
            try
            {
                var route = MeshTypeDetector.Detect(file);
                if (!route.IsSupported || route.Kind != MeshFileKind.Collision)
                {
                    failures.Add($"{file}: {route.UnsupportedReason ?? "not routed as collision"}");
                    continue;
                }

                var data = File.ReadAllBytes(file);
                if (!ColFile.IsColFile(data))
                {
                    failures.Add($"{file}: signature rejected");
                    continue;
                }

                var scene = ColFile.Parse(data);
                objects += scene.Objects.Length;
                vertices += scene.TotalVertices;
                triangles += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{file}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} X360 COL files failed:\n" +
            string.Join("\n", failures.Take(20)));
        Assert.Equal(32_034, objects);
        Assert.Equal(1_268_567, vertices);
        Assert.Equal(996_233, triangles);
    }

    [CorpusFact]
    public void ZChNet_EndianMirrorMatchesPcAndExportsEveryNondegenerateTriangleToGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var xenPath = paths.FindSampleFile(XenBuild, "4214D375.col.xen");
        var pcPath = paths.FindSampleFile(PcBuild, "001AA400.col");
        Assert.SkipWhen(xenPath is null, "4214D375.col.xen not found");
        Assert.SkipWhen(pcPath is null, "001AA400.col not found");
        Assert.Equal(198_846, new FileInfo(xenPath).Length);
        Assert.Equal(198_846, new FileInfo(pcPath).Length);

        var xen = ColFile.Parse(xenPath);
        var pc = ColFile.Parse(pcPath);
        Assert.Equal(258, xen.Objects.Length);
        Assert.Equal(8_668, xen.TotalVertices);
        Assert.Equal(6_918, xen.TotalTriangles);
        Assert.Equal(0x9B2C8826u, xen.Objects[0].Checksum);
        Assert.Equal(pc.Objects.Length, xen.Objects.Length);

        for (var i = 0; i < pc.Objects.Length; i++)
        {
            var expected = pc.Objects[i];
            var actual = xen.Objects[i];
            Assert.Equal(expected.Checksum, actual.Checksum);
            Assert.Equal(expected.Flags, actual.Flags);
            Assert.Equal(expected.BBoxMin, actual.BBoxMin);
            Assert.Equal(expected.BBoxMax, actual.BBoxMax);
            Assert.Equal(expected.Vertices, actual.Vertices);
            Assert.Equal(expected.Faces, actual.Faces);
            Assert.Equal(expected.Intensities, actual.Intensities);
        }

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(xenPath),
            FileName = Path.GetFileName(xenPath),
            OutputStem = "4214D375",
            SourceKind = ModelSourceKind.Collision
        });
        var (glbBytes, triangleCount) = new GltfModelExporter().BuildGlbBytes(document);
        // Six source faces repeat a position and are intentionally suppressed by
        // the shared geometry adapter rather than emitted as zero-area glTF tris.
        Assert.Equal(6_912, document.TriangleCount);
        Assert.Equal(6_912, triangleCount);
        Assert.NotNull(glbBytes);

        var glb = ModelRoot.ReadGLB(new MemoryStream(glbBytes));
        Assert.Equal(
            6_912 * 3,
            glb.LogicalMeshes.SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.IndexAccessor!.Count));
    }

    private static byte[] CreateXenFile()
    {
        const int objectBase = 80;
        const int baseVert = 208; // Align16(80 + 2 * 64)
        const int baseIntensity = baseVert + 3 * 12 + 3 * 6;
        const int baseFace = 268; // Align4(baseIntensity + 6)
        var data = new byte[baseFace + 8 + 10 + 4];

        WriteInt32(data, 0, 10);
        WriteInt32(data, 4, 2);
        WriteInt32(data, 8, 6);
        WriteInt32(data, 12, 1);
        WriteInt32(data, 16, 1);
        WriteInt32(data, 20, 3);
        WriteInt32(data, 24, 3);
        // 48-byte supersector header at 32 remains zero, including finite bbox values.

        WriteObjectHeader(data, objectBase, 0x9B2C8826, 3, 1, true, true, 36, 0, 0,
            new System.Numerics.Vector3(100, 200, 300));
        WriteObjectHeader(data, objectBase + 64, 0x11223344, 3, 1, false, false, 0, 8, 3,
            new System.Numerics.Vector3(-100));

        for (var i = 0; i < 3; i++)
        {
            var offset = baseVert + i * 12;
            WriteSingle(data, offset, i * 10);
            WriteSingle(data, offset + 4, i * 10 + 1);
            WriteSingle(data, offset + 8, i * 10 + 2);
        }

        for (var i = 0; i < 3; i++)
        {
            var offset = baseVert + 36 + i * 6;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)(16 + i));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), (ushort)(32 + i));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4), (ushort)(48 + i));
        }

        data[baseIntensity] = 11;
        data[baseIntensity + 1] = 22;
        data[baseIntensity + 2] = 33;
        data[baseIntensity + 3] = 44;
        data[baseIntensity + 4] = 55;
        data[baseIntensity + 5] = 66;

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace), 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 2), 0x5678);
        data[baseFace + 4] = 2;
        data[baseFace + 5] = 0;
        data[baseFace + 6] = 1;

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 8), 0x4321);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 10), 0x8765);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 12), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(baseFace + 16), 2);
        return data;
    }

    private static void WriteObjectHeader(
        Span<byte> data,
        int offset,
        uint checksum,
        ushort vertices,
        ushort faces,
        bool smallFaces,
        bool fixedVertices,
        uint firstVertex,
        uint firstFace,
        uint intensity,
        System.Numerics.Vector3 bboxMin)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data[offset..], checksum);
        BinaryPrimitives.WriteUInt16BigEndian(data[(offset + 6)..], vertices);
        BinaryPrimitives.WriteUInt16BigEndian(data[(offset + 8)..], faces);
        data[offset + 10] = smallFaces ? (byte)1 : (byte)0;
        data[offset + 11] = fixedVertices ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(data[(offset + 12)..], firstFace);
        WriteSingle(data, offset + 16, bboxMin.X);
        WriteSingle(data, offset + 20, bboxMin.Y);
        WriteSingle(data, offset + 24, bboxMin.Z);
        WriteSingle(data, offset + 28, 1);
        WriteSingle(data, offset + 32, bboxMin.X + 1_000);
        WriteSingle(data, offset + 36, bboxMin.Y + 1_000);
        WriteSingle(data, offset + 40, bboxMin.Z + 1_000);
        WriteSingle(data, offset + 44, 1);
        BinaryPrimitives.WriteUInt32BigEndian(data[(offset + 48)..], firstVertex);
        BinaryPrimitives.WriteUInt32BigEndian(data[(offset + 56)..], intensity);
    }

    private static void WriteInt32(Span<byte> data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data[offset..], value);
    }

    private static void WriteSingle(Span<byte> data, int offset, float value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data[offset..], BitConverter.SingleToInt32Bits(value));
    }
}
