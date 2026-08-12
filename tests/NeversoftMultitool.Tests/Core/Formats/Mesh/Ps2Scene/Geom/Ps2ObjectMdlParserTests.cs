using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2ObjectMdlParserTests(TestPaths paths, ITestOutputHelper output)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [Fact]
    public void ParsePakMdl_ProvenCompactDmaChains_DecodesEachBoundedChain()
    {
        var data = BuildProvenTableLayout();

        Assert.Equal(0x2C, Ps2GeomMdlBatchScanner.FindMdlVifStart(data));
        var ranges = Ps2GeomMdlBatchScanner.FindProvenCompactDmaChainBatchRanges(data, 0x2C);
        Assert.Equal(2, ranges.Count);

        var rejections = new List<Ps2GeomLeafRejection>();
        var scene = Ps2GeomFile.ParsePakMdl(data, "synthetic_repeated_dma", rejections.Add);

        Assert.Empty(rejections);
        Assert.Collection(scene.Leaves,
            leaf =>
            {
                Assert.Equal(4, leaf.Vertices.Length);
                Assert.All(leaf.Vertices, vertex => Assert.Equal(0f, vertex.Position.Z));
            },
            leaf =>
            {
                Assert.Equal(36, leaf.Vertices.Length);
                Assert.All(leaf.Vertices, vertex => Assert.Equal(1f, vertex.Position.Z));
            });

        var document = new ModelDocument { Name = "synthetic_repeated_dma" };
        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, null, null);
        Assert.Equal(20, document.TriangleCount);
    }

    [Fact]
    public void FindProvenCompactDmaChainBatchRanges_SingleChain_PreservesLegacyFallback()
    {
        var data = BuildObjectMdl(chainCount: 1);

        Assert.Empty(Ps2GeomMdlBatchScanner.FindProvenCompactDmaChainBatchRanges(data, 0x2C));

        var scene = Ps2GeomFile.ParsePakMdl(data);
        var leaf = Assert.Single(scene.Leaves);
        Assert.Equal(4, leaf.Vertices.Length);
    }

    [Fact]
    public void FindProvenCompactDmaChainBatchRanges_UnprovenTriangleLayout_PreservesLegacyFallback()
    {
        var data = BuildProvenTableLayout(secondChainSuppressedTriangles: 17);

        Assert.Empty(Ps2GeomMdlBatchScanner.FindProvenCompactDmaChainBatchRanges(data, 0x2C));
    }

    [Fact]
    public void ParsePakMdl_AdditionalProvenCompactLayouts_DecodeAllReferenceTriangles()
    {
        var cases = new[]
        {
            (Data: BuildProvenCompactLayout(1328, (0x290, 16), (0x140, 2)), Triangles: 18),
            (Data: BuildProvenCompactLayout(1920, (0x2F0, 12), (0x1E0, 6), (0x100, 2)), Triangles: 20),
            (Data: BuildProvenCompactLayout(2192, (0x370, 12), (0x3C0, 16)), Triangles: 28)
        };

        foreach (var (data, expectedTriangles) in cases)
        {
            var ranges = Ps2GeomMdlBatchScanner.FindProvenCompactDmaChainBatchRanges(data, 0x2C);
            Assert.NotEmpty(ranges);

            var scene = Ps2GeomFile.ParsePakMdl(data, "synthetic_compact_reference");
            var document = new ModelDocument { Name = "synthetic_compact_reference" };
            Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, null, null);
            Assert.Equal(expectedTriangles, document.TriangleCount);
        }
    }

    [CorpusFact]
    public void ParsePakMdl_AllThawPs2WorldzoneObjectMdls_ExpandedLayoutsAreBounded()
    {
        var pakPaths = paths.FindSampleFiles(ThawPs2Build, "*.pak.ps2")
            .Where(static path => path.Replace('\\', '/').Contains(
                "/worlds/worldzones/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.SkipWhen(pakPaths.Count == 0, "THAW PS2 worldzone PAK corpus not available");

        var referenceTrianglesByLength = new Dictionary<int, (int Legacy, int Complete)>
        {
            [1328] = (16, 18),
            [1920] = (12, 20),
            [2192] = (12, 28)
        };
        var objectMdlCount = 0;
        var pakMdlCount = 0;
        var expandedLengthCandidateCount = 0;
        var structurallyAcceptedCount = 0;
        var parseFailureCount = 0;
        var recoveredTriangleCount = 0;
        var acceptedHashes = new HashSet<string>(StringComparer.Ordinal);
        var acceptedByLength = new Dictionary<int, int>();

        foreach (var pakPath in pakPaths)
        {
            var pakBytes = File.ReadAllBytes(pakPath);
            foreach (var (typeHash, entry) in PakArchive.GetTypedEntries(pakBytes))
            {
                if (typeHash != Ps2WorldzoneDetection.WorldzoneMdlTypeHash ||
                    entry.Offset < 0 || entry.Size <= 0 || entry.Offset + entry.Size > pakBytes.Length)
                {
                    continue;
                }

                objectMdlCount++;
                var data = pakBytes.AsSpan(
                    checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
                if (!Ps2GeomFile.IsPakMdl(data))
                    continue;

                pakMdlCount++;
                try
                {
                    _ = Ps2GeomFile.ParsePakMdl(data, entry.Name);
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                {
                    parseFailureCount++;
                    continue;
                }

                if (!referenceTrianglesByLength.TryGetValue(data.Length, out var reference) ||
                    Ps2GeomMdlBatchScanner.FindMdlVifStart(data) != 0x2C)
                {
                    continue;
                }

                expandedLengthCandidateCount++;
                var ranges = Ps2GeomMdlBatchScanner.FindProvenCompactDmaChainBatchRanges(data, 0x2C);
                if (ranges.Count == 0)
                    continue;

                structurallyAcceptedCount++;
                acceptedByLength[data.Length] = acceptedByLength.GetValueOrDefault(data.Length) + 1;
                acceptedHashes.Add(Convert.ToHexString(SHA256.HashData(data)));

                var legacyTriangles = Ps2GeomMdlBatchScanner.FindMscalBatchRanges(data, 0x2C, data.Length)
                    .Sum(range => CountVifRangeTriangles(data, range));
                var completeScene = Ps2GeomFile.ParsePakMdl(data, entry.Name);
                var completeTriangles = completeScene.Leaves.Sum(CountRenderableStripTriangles);
                Assert.Equal(reference.Legacy, legacyTriangles);
                Assert.Equal(reference.Complete, completeTriangles);
                recoveredTriangleCount += completeTriangles - legacyTriangles;
            }
        }

        output.WriteLine(
            $"THAW PS2 object-MDL sweep: archives={pakPaths.Count}, entries={objectMdlCount}, " +
            $"PAK-MDLs={pakMdlCount}, expanded-length candidates={expandedLengthCandidateCount}, " +
            $"structural changes={structurallyAcceptedCount}, distinct payloads={acceptedHashes.Count}, " +
            $"duplicates={structurallyAcceptedCount - acceptedHashes.Count}, " +
            $"recovered resource triangles={recoveredTriangleCount}, parse regressions={parseFailureCount}, " +
            $"by-length={string.Join(',', acceptedByLength.OrderBy(static item => item.Key).Select(static item => $"{item.Key}:{item.Value}"))}");

        Assert.Equal(128, pakPaths.Count);
        Assert.Equal(60, objectMdlCount);
        Assert.Equal(60, pakMdlCount);
        Assert.Equal(7, expandedLengthCandidateCount);
        Assert.Equal(7, structurallyAcceptedCount);
        Assert.Equal(3, acceptedHashes.Count);
        Assert.Equal(4, structurallyAcceptedCount - acceptedHashes.Count);
        Assert.Equal(62, recoveredTriangleCount);
        Assert.Equal(3, acceptedByLength.Count);
        Assert.Equal(3, acceptedByLength[1328]);
        Assert.Equal(1, acceptedByLength[1920]);
        Assert.Equal(3, acceptedByLength[2192]);
        Assert.Equal(0, parseFailureCount);
    }

    [Fact]
    public void ParsePakMdl_ZBhCompactProps_WorldzoneRootPassEmitsFourteenTriangles()
    {
        const string build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
        var pakPath = paths.FindSampleFile(build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["0003D1D0.mdl"] = 0,
            ["00040420.mdl"] = 2,
            ["00042FB0.mdl"] = 0,
            ["00045120.mdl"] = 6,
            ["00047960.mdl"] = 6
        };
        var pakBytes = File.ReadAllBytes(pakPath!);
        var typedEntries = PakArchive.GetTypedEntries(pakBytes);
        var totalTriangles = 0;

        foreach (var (name, expectedTriangles) in expected)
        {
            var typedEntry = Assert.Single(typedEntries,
                entry => entry.Entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var data = pakBytes.AsSpan(
                checked((int)typedEntry.Entry.Offset),
                checked((int)typedEntry.Entry.Size)).ToArray();
            var scene = Ps2GeomFile.ParsePakMdl(data, name);
            Assert.Empty(scene.Bones ?? []);

            var document = new ModelDocument { Name = name };
            var materialCache =
                new Dictionary<Ps2WorldzoneMaterialWriter.Ps2WorldzoneMaterialKey, int>();
            Ps2WorldzoneGeometryWriter.PopulatePs2WorldzoneLeaves(
                document,
                scene,
                Path.GetFileNameWithoutExtension(name),
                [(Vector3.Zero, Quaternion.Identity)],
                static leaf => !leaf.IsLocalSpace,
                materialCache,
                null,
                null,
                null,
                1f,
                "world");

            var triangles = document.Meshes
                .SelectMany(static mesh => mesh.Primitives)
                .Sum(static primitive => primitive.TriangleCount);
            Assert.Equal(expectedTriangles, triangles);
            totalTriangles += triangles;
        }

        Assert.Equal(14, totalTriangles);
    }

    private static byte[] BuildProvenTableLayout(int secondChainSuppressedTriangles = 16)
    {
        var data = new List<byte>(new byte[0x20]);
        AddDmaChain(data, z: 0, vertexCount: 4, suppressedTriangles: 0, totalLength: 0x100);
        Assert.Equal(0x120, data.Count);
        AddDmaChain(
            data,
            z: 16,
            vertexCount: 36,
            suppressedTriangles: secondChainSuppressedTriangles,
            totalLength: 0x440);
        Assert.Equal(0x560, data.Count);

        while (data.Count < 0x6A0)
            data.Add(0);

        var result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), 0x550);
        return result;
    }

    private static byte[] BuildObjectMdl(int chainCount)
    {
        var data = new List<byte>(new byte[0x20]);
        for (var i = 0; i < chainCount; i++)
            AddDmaChain(
                data,
                z: checked((short)(i * 16)),
                vertexCount: 4,
                suppressedTriangles: 0,
                totalLength: 0x60);

        while (data.Count < 0x100)
            data.Add(0);

        var result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), checked((uint)(0x10 + chainCount * 0x60)));
        return result;
    }

    private static byte[] BuildProvenCompactLayout(
        int fileLength,
        params (int TotalLength, int Triangles)[] chains)
    {
        var data = new List<byte>(new byte[0x20]);
        for (var i = 0; i < chains.Length; i++)
        {
            var (totalLength, triangles) = chains[i];
            AddDmaChain(
                data,
                z: checked((short)(i * 16)),
                vertexCount: triangles + 2,
                suppressedTriangles: 0,
                totalLength: totalLength);
        }

        var geometryEnd = data.Count;
        Assert.True(geometryEnd <= fileLength);
        while (data.Count < fileLength)
            data.Add(0);

        var result = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), checked((uint)(geometryEnd - 0x10)));
        return result;
    }

    private static int CountVifRangeTriangles(byte[] data, (int Start, int End) range)
    {
        var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(
            data, range.Start, range.End, Vector3.Zero);
        return CountRenderableStripTriangles(new Ps2GeomLeaf { Vertices = vertices });
    }

    private static int CountRenderableStripTriangles(Ps2GeomLeaf leaf)
    {
        var mesh = new ModelMesh { Name = "triangle_count" };
        return Ps2SceneGeometryWriter.AddPs2StripPrimitive(
            mesh,
            "strip",
            materialIndex: 0,
            leaf.Vertices,
            startsOnOddOutputSlot: false,
            dedup: null,
            preserveVertexAlpha: true,
            bakeVertexColorsToWhite: false)?.TriangleCount ?? 0;
    }

    private static void AddDmaChain(
        List<byte> data,
        short z,
        int vertexCount,
        int suppressedTriangles,
        int totalLength)
    {
        var payload = new List<byte>();

        // Standard object-MDL GIF-tag upload used by FindMdlVifStart.
        AddVifCode(payload, 0xC000, 1, 0x6C);
        payload.AddRange(new byte[16]);

        AddVifCode(payload, 1, 0, 0x05); // STMOD(1): next V4 UNPACK contains positions.
        AddVifCode(payload, 0x8009, checked((byte)vertexCount), 0x6D); // UNPACK V4_16.
        for (var i = 0; i < vertexCount; i++)
        {
            var suppressesTriangle = i < 2 + suppressedTriangles;
            AddPosition(
                payload,
                checked((short)(i * 16)),
                checked((short)((i & 1) * 16)),
                z,
                suppressesTriangle ? (ushort)0x8000 : (ushort)0);
        }
        AddVifCode(payload, 0, 0, 0x05); // STMOD(0).
        AddVifCode(payload, 0, 0, 0x14); // MSCAL.

        var payloadLength = totalLength - 16;
        Assert.True(payload.Count <= payloadLength);
        while (payload.Count < payloadLength)
            payload.Add(0);

        var qwc = checked((ushort)(payload.Count / 16));
        Span<byte> dma = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(dma, (6u << 28) | qwc);
        BinaryPrimitives.WriteUInt32LittleEndian(dma[8..], 0x02000000); // OFFSET(0).
        BinaryPrimitives.WriteUInt32LittleEndian(dma[12..], 0x01000101); // STCYCL(1,1).
        foreach (var b in dma)
            data.Add(b);
        data.AddRange(payload);
    }

    private static void AddVifCode(List<byte> data, ushort imm, byte num, byte cmd)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, imm);
        buffer[2] = num;
        buffer[3] = cmd;
        foreach (var b in buffer)
            data.Add(b);
    }

    private static void AddPosition(List<byte> data, short x, short y, short z, ushort w)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, x);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[2..], y);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[4..], z);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], w);
        foreach (var b in buffer)
            data.Add(b);
    }
}
