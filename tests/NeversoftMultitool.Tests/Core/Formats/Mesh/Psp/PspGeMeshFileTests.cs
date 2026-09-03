using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Psp;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psp;

public sealed class PspGeMeshFileTests(TestPaths paths)
{
    private const string RemixBuild =
        "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";

    private const string Project8FinalBuild =
        "Tony Hawk's Project 8 (2006-10-14, PSP - Final)";

    private const string Project8Rev1Build =
        "Tony Hawk's Project 8 (2007-2-16, PSP - Rev1)";

    [Fact]
    public void Parse_TriangleList_DecodesTheProvenVertexLayoutAndPortableBasis()
    {
        var data = CreateTrianglePayload();

        var parsed = PspGeMeshFile.Parse(data);

        Assert.Equal(0, parsed.Summary.PayloadOffset);
        Assert.Equal(data.Length, parsed.Summary.PayloadSize);
        Assert.Equal(1, parsed.Summary.PrimitiveCount);
        Assert.Equal(3, parsed.Summary.VertexCount);
        Assert.Equal(0, parsed.Summary.WeightedVertexCount);
        Assert.Equal(1, parsed.Summary.TheoreticalTriangleCount);

        var sector = Assert.Single(parsed.Scene.Sectors);
        var mesh = Assert.Single(sector.Meshes);
        Assert.True(mesh.IsPreTriangulated);
        Assert.Equal([0, 1, 2], mesh.FaceIndices);
        Assert.Equal(Vector3.Zero, mesh.Vertices[0].Position);
        Assert.Equal(new Vector3(1, 0, 0), mesh.Vertices[1].Position);
        // PSP source Z is portable Y; source Y becomes -portable Z.
        Assert.Equal(new Vector3(0, 1, 0), mesh.Vertices[2].Position);
        Assert.Equal(Vector3.UnitY, mesh.Vertices[0].Normal);
        Assert.Equal(Vector4.One, mesh.Vertices[0].Color);
        Assert.False(mesh.Vertices[0].HasSkinData);
    }

    [Fact]
    public void MeshModelParser_ExportsTheRigidSceneThroughTheSharedPipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-psp-ge-{Guid.NewGuid():N}.skin.psp");
        try
        {
            File.WriteAllBytes(path, CreateTrianglePayload());
            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = new FileSystemAssetSource(path),
                FileName = Path.GetFileName(path),
                OutputStem = "psp_triangle",
                SourceKind = ModelSourceKind.XbxScene
            });

            Assert.Equal(ModelSourceKind.XbxScene, document.SourceKind);
            Assert.Equal(1, document.TriangleCount);
            Assert.Single(document.Meshes);
            Assert.Empty(document.Skeletons);
            Assert.Single(document.Materials);

            var (glbBytes, triangleCount) = new GltfModelExporter().BuildGlbBytes(document);
            Assert.Equal(1, triangleCount);
            Assert.True(glbBytes.Length > 12);
            Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(glbBytes, 0, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("asset.skin.psp", false)]
    [InlineData("asset.mdl.psp", false)]
    [InlineData("asset.geom.psp", true)]
    [InlineData("asset.skin", false)]
    [InlineData("asset.mdl", true)]
    public void Detector_ContentGatesEveryProvenName(string fileName, bool wrapped)
    {
        var data = wrapped ? Wrap(CreateTrianglePayload()) : CreateTrianglePayload();

        var route = MeshTypeDetector.DetectFromBytes(fileName, data, data.Length);

        Assert.True(route.IsSupported, route.UnsupportedReason);
        Assert.Equal(MeshFileKind.XbxScene, route.Kind);
        Assert.Equal("PSP GE Mesh (rigid bind pose)", route.DisplayFormat);
        Assert.Equal("asset", MeshTypeDetector.GetStem(fileName));
        Assert.True(MeshTypeDetector.ReportsPartialSupport(route));
    }

    [Fact]
    public void Parse_OneValidatedPayloadInsideAWrapper_AllowsPrefixAndTrailer()
    {
        var wrapped = Wrap(CreateTrianglePayload());

        var parsed = PspGeMeshFile.Parse(wrapped);

        Assert.Equal(0xC0, parsed.Summary.PayloadOffset);
        Assert.Equal(3, parsed.Summary.VertexCount);
    }

    [Fact]
    public void Parse_DirectPayloadWithUnclaimedTrailer_IsRejected()
    {
        var data = CreateTrianglePayload();
        Array.Resize(ref data, data.Length + 4);

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("Direct PSP GE mesh consumes", error.Message);
        Assert.False(PspGeMeshFile.TryInspect(data, out _, out _));
    }

    [Fact]
    public void Parse_BrokenSectionChain_IsRejected()
    {
        var data = CreateTrianglePayload();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), 0x64);

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("sections are not contiguous", error.Message);
    }

    [Fact]
    public void Parse_PrimBeforeVtype_IsRejected()
    {
        var data = CreateTrianglePayload();
        WriteCommand(data, 0x60, 0x04, (3u << 16) | 3u);

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("PRIM appears before VTYPE", error.Message);
    }

    [Fact]
    public void Parse_IndexedVertexType_IsRejectedInsteadOfGuessing()
    {
        var data = CreateTrianglePayload();
        WriteCommand(data, 0x60, 0x12, 0x938); // 0x138 plus 8-bit index mode.

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("Unsupported PSP GE VTYPE", error.Message);
    }

    [Fact]
    public void Parse_VertexBufferThatDoesNotMatchTheDisplayList_IsRejected()
    {
        var data = CreateTrianglePayload();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24), 40);
        Array.Resize(ref data, data.Length + 4);

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("consumes 36 of 40 vertex bytes", error.Message);
    }

    [Fact]
    public void Parse_TwoStructurallyValidEmbeddedPayloads_AreRejectedAsAmbiguous()
    {
        var payload = CreateTrianglePayload();
        var data = new byte[0xC0 + payload.Length * 2];
        payload.CopyTo(data, 0xC0);
        payload.CopyTo(data, 0xC0 + payload.Length);

        var error = Assert.Throws<InvalidDataException>(() => PspGeMeshFile.Parse(data));

        Assert.Contains("more than one structurally valid", error.Message);
    }

    [Fact]
    public void Parse_MagicAloneAndOverflowingOffsets_AreRejectedWithoutThrowingFromProbe()
    {
        var data = new byte[0x60];
        BinaryPrimitives.WriteUInt32LittleEndian(data, PspGeMeshFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), uint.MaxValue);

        Assert.False(PspGeMeshFile.TryInspect(data, out _, out var error));
        Assert.NotNull(error);
        var route = MeshTypeDetector.DetectFromBytes("bad.skin.psp", data, data.Length);
        Assert.False(route.IsSupported);
        Assert.Equal("PSP GE Mesh", route.DisplayFormat);
    }

    /// <summary>
    ///     Full loose-corpus gate. The three PSP builds contain 10,052 files with
    ///     one of the proven names: 9,509 hold one exact-consuming payload and
    ///     543 are authored-empty/name-collision wrappers. Fourteen valid payloads
    ///     contain no vertices; they remain successful empty models.
    /// </summary>
    [CorpusFact]
    public void PspCorpus_EveryNamedFileIsEitherOneExactPayloadOrARejectedEmptyWrapper()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = new[] { RemixBuild, Project8FinalBuild, Project8Rev1Build }
            .SelectMany(FindCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(10052, files.Length);

        var supported = 0;
        var nonempty = 0;
        var validEmpty = 0;
        var rejected = 0;
        long vertices = 0;
        long primitives = 0;
        long theoreticalTriangles = 0;
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!PspGeMeshFile.TryInspect(data, out var summary, out _))
            {
                rejected++;
                Assert.False(MeshTypeDetector.Detect(file).IsSupported);
                continue;
            }

            supported++;
            if (summary.VertexCount == 0)
                validEmpty++;
            else
                nonempty++;
            vertices += summary.VertexCount;
            primitives += summary.PrimitiveCount;
            theoreticalTriangles += summary.TheoreticalTriangleCount;

            var route = MeshTypeDetector.Detect(file);
            Assert.True(route.IsSupported, $"{file}: {route.UnsupportedReason}");
            Assert.Equal("PSP GE Mesh (rigid bind pose)", route.DisplayFormat);
        }

        Assert.Equal("9509/9495/14/543", $"{supported}/{nonempty}/{validEmpty}/{rejected}");
        Assert.Equal(6_894_277, vertices);
        Assert.Equal(1_023_922, primitives);
        Assert.Equal(4_600_331, theoreticalTriangles);
    }

    [CorpusTheory]
    [InlineData(RemixBuild, "arrow.mdl.psp")]
    [InlineData(Project8FinalBuild, "00000860.mdl")]
    [InlineData(Project8Rev1Build, "00000860.mdl")]
    public void RepresentativeCorpusPayloads_BuildNonemptyPortableGeometry(string build, string fileName)
    {
        var path = paths.FindSampleFile(build, fileName);
        Assert.SkipWhen(path == null, $"{build}/{fileName} not present");
        var parsed = PspGeMeshFile.Parse(File.ReadAllBytes(path!));

        Assert.True(parsed.Summary.VertexCount > 0);
        Assert.NotEmpty(parsed.Scene.Sectors);
        Assert.Contains(parsed.Scene.Sectors, static sector => sector.Meshes.Length > 0);
    }

    private IEnumerable<string> FindCandidates(string build)
    {
        var root = Path.Combine(paths.SampleBuildsDir!, build);
        Assert.True(Directory.Exists(root), $"PSP corpus build missing: {build}");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static file =>
            {
                var name = Path.GetFileName(file);
                return name.EndsWith(".skin.psp", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".mdl.psp", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".geom.psp", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".skin", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static byte[] Wrap(byte[] payload)
    {
        var data = new byte[0xC0 + payload.Length + 12];
        // The real wrapper begins with this fixed little-endian record pair; the
        // payload parser deliberately relies on its own stronger invariants.
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x10);
        payload.CopyTo(data, 0xC0);
        return data;
    }

    private static byte[] CreateTrianglePayload()
    {
        const int displayListOffset = 0x60;
        const int displayListSize = 0x10;
        const int vertexBufferOffset = displayListOffset + displayListSize;
        const int vertexStride = 12;
        var data = new byte[vertexBufferOffset + vertexStride * 3];

        BinaryPrimitives.WriteUInt32LittleEndian(data, PspGeMeshFile.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), vertexBufferOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24), vertexStride * 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), displayListOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), displayListSize);

        WriteCommand(data, displayListOffset, 0x12, 0x138); // RGBA4444 + s8 normal + s16 position.
        WriteCommand(data, displayListOffset + 4, 0x04, (3u << 16) | 3u);
        WriteCommand(data, displayListOffset + 8, 0xFF, 0);
        WriteCommand(data, displayListOffset + 12, 0x0B, 0);

        WriteVertex(data, vertexBufferOffset, 0, 0, 0);
        WriteVertex(data, vertexBufferOffset + vertexStride, 16, 0, 0);
        WriteVertex(data, vertexBufferOffset + vertexStride * 2, 0, 0, 16);
        return data;
    }

    private static void WriteVertex(byte[] data, int offset, short x, short y, short z)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), 0xFFFF);
        data[offset + 2] = 0;
        data[offset + 3] = 0;
        data[offset + 4] = 127;
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 6), x);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 8), y);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 10), z);
    }

    private static void WriteCommand(byte[] data, int offset, byte command, uint argument)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(offset),
            ((uint)command << 24) | (argument & 0x00FFFFFF));
    }
}
