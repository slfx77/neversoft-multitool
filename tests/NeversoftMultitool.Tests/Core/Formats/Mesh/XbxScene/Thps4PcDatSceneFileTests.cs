using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class Thps4PcDatSceneFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";
    private const uint MaterialChecksum = 0x10203040;

    [Theory]
    [InlineData("Anl_Chickenskin.dat", true, "Anl_Chicken")]
    [InlineData("ArrowMDL.DAT", true, "Arrow")]
    [InlineData("Alc_Skyscn.dat", true, "Alc_Sky")]
    [InlineData("skin.dat", false, "skin")]
    [InlineData("model.skin.dat", false, "model.skin")]
    [InlineData("model.mdl.dat", false, "model.mdl")]
    [InlineData("level.scn.dat", false, "level.scn")]
    [InlineData("alctex.dat", false, "alctex")]
    public void NameGate_RequiresADelimiterFreeNonEmptyStem(
        string name,
        bool expected,
        string expectedStem)
    {
        Assert.Equal(expected, Thps4PcDatSceneFile.IsCandidateFileName(name));
        Assert.Equal(expected, MeshTypeDetector.IsMeshCandidate(name));
        Assert.Equal(expectedStem, MeshTypeDetector.GetStem(name));
    }

    [Fact]
    public void Parse_DecodesPlanarPoolMaterialStripAndThps4ColorScale()
    {
        var scene = Thps4PcDatSceneFile.Parse(BuildScene());

        var material = Assert.Single(scene.Materials);
        Assert.Equal(MaterialChecksum, material.Checksum);
        Assert.Equal(MaterialChecksum, material.NameChecksum);
        Assert.Equal(1, material.NumPasses);
        Assert.Equal(64, material.AlphaCutoff);
        Assert.True(material.Sorted);
        Assert.True(material.SingleSided);
        Assert.False(material.NoBfc);
        var pass = Assert.Single(material.Passes);
        Assert.Equal(0x55667788u, pass.TextureChecksum);
        Assert.Equal(4u, pass.Flags);
        Assert.Equal(128u, pass.FixedAlpha);

        var sector = Assert.Single(scene.Sectors);
        Assert.Equal(3, sector.SourceVertexCount);
        Assert.Equal(28u, sector.SourceVertexStride);
        Assert.Equal(1, sector.SourceUvSetCount);
        var mesh = Assert.Single(sector.Meshes);
        Assert.Equal([0, 1, 2], mesh.FaceIndices);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(new Vector3(1, 0, 0), mesh.Vertices[1].Position);
        Assert.Equal(new Vector2(1, 0), mesh.Vertices[1].TexCoord);
        Assert.Equal(30f / 255f, mesh.Vertices[0].Color.X, 6);
        Assert.Equal(20f / 255f, mesh.Vertices[0].Color.Y, 6);
        Assert.Equal(10f / 255f, mesh.Vertices[0].Color.Z, 6);
        Assert.Equal(64f / 128f, mesh.Vertices[0].Color.W, 6);
        Assert.Equal(1, scene.TotalTriangles);
        Assert.Empty(scene.Links);
        Assert.False(scene.ApplyHierarchyTransforms);
    }

    [Fact]
    public void Parse_CompactsOnlyVerticesReferencedByEachMesh()
    {
        var scene = Thps4PcDatSceneFile.Parse(BuildScene(indices: [2, 0, 2]));

        var mesh = Assert.Single(Assert.Single(scene.Sectors).Meshes);
        Assert.Equal(2, mesh.Vertices.Length);
        Assert.Equal(new Vector3(0, 1, 0), mesh.Vertices[0].Position);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Vertices[1].Position);
        Assert.Equal([0, 1, 0], mesh.FaceIndices);
    }

    [Fact]
    public void Parse_AuthoredNonFiniteUvIsSanitizedForPortableExport()
    {
        var scene = Thps4PcDatSceneFile.Parse(BuildScene(nonFiniteUv: true));

        Assert.Equal(Vector2.Zero, Assert.Single(Assert.Single(scene.Sectors).Meshes).Vertices[0].TexCoord);
    }

    [Fact]
    public void Parse_RequiresEveryByteAndEveryReferencedVertex()
    {
        var complete = BuildScene();
        Assert.True(Thps4PcDatSceneFile.TryParse(complete, out _, out _));

        var trailing = complete.Concat([byte.MinValue]).ToArray();
        Assert.False(Thps4PcDatSceneFile.TryParse(trailing, out _, out var trailingError));
        Assert.Contains("hierarchy", trailingError, StringComparison.OrdinalIgnoreCase);

        for (var length = 0; length < complete.Length; length++)
        {
            Assert.False(
                Thps4PcDatSceneFile.TryParse(complete[..length], out _, out _),
                $"truncated prefix of {length} bytes was accepted");
        }

        var invalidIndex = BuildScene(indices: [0, 1, 3]);
        Assert.False(Thps4PcDatSceneFile.TryParse(invalidIndex, out _, out var indexError));
        Assert.Contains("only 3 vertices", indexError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_VersionTripleAloneIsNotAFormatSignature()
    {
        var falseFriend = new byte[256];
        BitConverter.GetBytes(1u).CopyTo(falseFriend, 0);
        BitConverter.GetBytes(1u).CopyTo(falseFriend, 4);
        BitConverter.GetBytes(1u).CopyTo(falseFriend, 8);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(falseFriend, 12);

        Assert.True(XbxSceneFile.IsXbxScene(falseFriend));
        Assert.False(Thps4PcDatSceneFile.TryParse(falseFriend, out _, out var error));
        Assert.Contains("material count", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Hawkskin.dat", "THPS4 PC Skin")]
    [InlineData("Arrowmdl.dat", "THPS4 PC Model")]
    [InlineData("Alcscn.dat", "THPS4 PC Level Scene")]
    public void Detector_RequiresTheCompleteStrictPayload(string name, string expectedFormat)
    {
        var data = BuildScene();
        var route = MeshTypeDetector.DetectFromBytes(name, data, data.Length);

        Assert.True(route.IsSupported, route.UnsupportedReason);
        Assert.Equal(MeshFileKind.XbxScene, route.Kind);
        Assert.False(route.RequiresContentProbe);
        Assert.Equal(expectedFormat, route.DisplayFormat);
        Assert.Equal(int.MaxValue, MeshTypeDetector.GetProbeByteBudget(name));

        var prefix = MeshTypeDetector.DetectFromBytes(name, data[..^1], data.Length);
        Assert.False(prefix.IsSupported);
        Assert.Contains("complete payload", prefix.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hierarchy_DecodesSignedParentAndBoneFieldsAndLocalMatrices()
    {
        var scene = Thps4PcDatSceneFile.Parse(BuildScene(withHierarchy: true));

        Assert.True(scene.ApplyHierarchyTransforms);
        Assert.Equal(2, scene.Links.Length);
        Assert.Equal(-1, scene.Links[0].ParentIndex);
        Assert.Equal(0, scene.Links[0].BoneIndex);
        Assert.Equal(new Vector3(10, 0, 0), scene.Links[0].Transform.Translation);
        Assert.Equal(0, scene.Links[1].ParentIndex);
        Assert.Equal(1, scene.Links[1].BoneIndex);
        Assert.Equal(scene.Links[0].SectorChecksum, scene.Links[1].ParentChecksum);
        Assert.Equal(new Vector3(0, 5, 0), scene.Links[1].Transform.Translation);
    }

    [Fact]
    public void MeshModelParser_UsesTheStrictDatRouteAndEmitsStaticGeometry()
    {
        var source = new MemorySource("Arrowmdl.dat", BuildScene());
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Arrow",
            SourceKind = ModelSourceKind.XbxScene
        });

        Assert.Equal(1, document.TriangleCount);
        Assert.Single(document.Meshes);
        Assert.Single(document.Nodes);
        Assert.Single(document.Materials);
    }

    [Fact]
    public void MeshModelParser_AppliesHierarchySetupMatricesInParentOrder()
    {
        var source = new MemorySource("Vehiclemdl.dat", BuildScene(withHierarchy: true));
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "Vehicle",
            SourceKind = ModelSourceKind.XbxScene
        });

        Assert.Equal(2, document.Nodes.Count);
        Assert.Equal(new Vector3(10, 0, 0), document.Nodes[0].Transform.Translation);
        Assert.Equal(new Vector3(10, 5, 0), document.Nodes[1].Transform.Translation);
    }

    [CorpusFact]
    public void Corpus_All601DelimiterFreeScenesParseAndRouteWithExactTotals()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var expectations = new[]
        {
            new CorpusExpectation("*skin.dat", 420, 8_280_691, 1_280, 420, 121_884, 1_280, 272_664, 158_355, 0),
            new CorpusExpectation("*mdl.dat", 152, 3_222_246, 1_452, 446, 62_889, 1_850, 100_166, 46_689, 151),
            new CorpusExpectation("*scn.dat", 29, 37_464_367, 8_282, 9_565, 1_121_412, 30_180, 1_698_870, 727_274, 0)
        };

        foreach (var expected in expectations)
        {
            var files = paths.FindSampleFiles(BuildName, expected.Pattern)
                .Where(file => Thps4PcDatSceneFile.IsCandidateFileName(Path.GetFileName(file)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(expected.Files, files.Length);

            long bytes = 0, materials = 0, sectors = 0, vertices = 0;
            long meshes = 0, indices = 0, triangles = 0, links = 0;
            var failures = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    var scene = Thps4PcDatSceneFile.Parse(data);
                    var route = MeshTypeDetector.DetectFromBytes(Path.GetFileName(file), data, data.Length);
                    if (!route.IsSupported || route.Kind != MeshFileKind.XbxScene)
                    {
                        failures.Add($"{Path.GetFileName(file)} route: {route.UnsupportedReason}");
                        continue;
                    }

                    bytes += data.Length;
                    materials += scene.Materials.Length;
                    sectors += scene.Sectors.Length;
                    vertices += scene.Sectors.Sum(static sector => sector.SourceVertexCount);
                    meshes += scene.Sectors.Sum(static sector => sector.Meshes.Length);
                    indices += scene.Sectors.Sum(static sector =>
                        sector.Meshes.Sum(static mesh => mesh.FaceIndices.Length));
                    triangles += scene.TotalTriangles;
                    links += scene.Links.Length;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Assert.Empty(failures);
            Assert.Equal(expected.Bytes, bytes);
            Assert.Equal(expected.Materials, materials);
            Assert.Equal(expected.Sectors, sectors);
            Assert.Equal(expected.SourceVertices, vertices);
            Assert.Equal(expected.Meshes, meshes);
            Assert.Equal(expected.Indices, indices);
            Assert.Equal(expected.Triangles, triangles);
            Assert.Equal(expected.Links, links);
        }
    }

    [CorpusFact]
    public void Corpus_All29SceneFilesPopulateRenderableDocuments()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(BuildName, "*scn.dat")
            .Where(file => Thps4PcDatSceneFile.IsCandidateFileName(Path.GetFileName(file)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(29, files.Length);

        long triangles = 0, meshes = 0, nodes = 0;
        foreach (var file in files)
        {
            var source = new MemorySource(Path.GetFileName(file), File.ReadAllBytes(file));
            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = source,
                FileName = source.EntryName,
                OutputStem = MeshTypeDetector.GetStem(source.EntryName),
                SourceKind = ModelSourceKind.XbxScene
            });
            triangles += document.TriangleCount;
            meshes += document.Meshes.Count;
            nodes += document.Nodes.Count;
        }

        Assert.Equal(726_332, triangles);
        Assert.Equal(30_129, meshes);
        Assert.Equal(30_129, nodes);
    }

    private static byte[] BuildScene(
        ushort[]? indices = null,
        bool nonFiniteUv = false,
        bool withHierarchy = false)
    {
        indices ??= [0, 1, 2];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u); // materials
        WriteMaterial(writer);

        var sectorCount = withHierarchy ? 2 : 1;
        writer.Write(sectorCount);
        for (var sector = 0; sector < sectorCount; sector++)
            WriteSector(writer, 0xA0000000u + (uint)sector, withHierarchy ? sector : -1, indices, nonFiniteUv);

        writer.Write(withHierarchy ? 2u : 0u);
        if (withHierarchy)
        {
            WriteHierarchyObject(writer, 0xA0000000, 0, -1, 0, Matrix4x4.CreateTranslation(10, 0, 0));
            WriteHierarchyObject(writer, 0xA0000001, 0xA0000000, 0, 1, Matrix4x4.CreateTranslation(0, 5, 0));
        }

        return stream.ToArray();
    }

    private static void WriteMaterial(BinaryWriter writer)
    {
        writer.Write(MaterialChecksum);
        writer.Write(1u); // passes
        writer.Write(64u); // alpha cutoff
        writer.Write((byte)1); // sorted
        writer.Write(2f); // draw order
        writer.Write((byte)1); // single-sided
        writer.Write((byte)0); // grass

        writer.Write(0x55667788u); // texture
        writer.Write(4u); // textured
        writer.Write((byte)1); // has color
        writer.Write(1f);
        writer.Write(0.5f);
        writer.Write(0.25f);
        writer.Write(0u); // blend
        writer.Write(128u); // fixed alpha
        writer.Write(0u); // U repeat
        writer.Write(0u); // V repeat
        writer.Write(0x00010004u); // filtering
        for (var i = 0; i < 9; i++) writer.Write(i == 0 ? 1f : 0f);
        writer.Write(0u); // MMAG
        writer.Write(0u); // MMIN
        writer.Write(0f); // K
        writer.Write(0f); // L
    }

    private static void WriteSector(
        BinaryWriter writer,
        uint checksum,
        int boneIndex,
        IReadOnlyList<ushort> indices,
        bool nonFiniteUv)
    {
        writer.Write(checksum);
        writer.Write(boneIndex);
        writer.Write(7u); // UV + color + normal
        writer.Write(1u); // meshes
        WriteVector3(writer, Vector3.Zero);
        WriteVector3(writer, Vector3.One);
        WriteVector3(writer, new Vector3(0.5f));
        writer.Write(1f);
        writer.Write(3u); // vertices
        writer.Write(28u); // source stride metadata

        WriteVector3(writer, Vector3.Zero);
        WriteVector3(writer, Vector3.UnitX);
        WriteVector3(writer, Vector3.UnitY);
        for (var i = 0; i < 3; i++) WriteVector3(writer, Vector3.UnitZ);
        writer.Write(1u); // UV sets
        writer.Write(nonFiniteUv ? float.NaN : 0f);
        writer.Write(nonFiniteUv ? float.PositiveInfinity : 0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        for (var i = 0; i < 3; i++)
        {
            writer.Write((byte)10); // B
            writer.Write((byte)20); // G
            writer.Write((byte)30); // R
            writer.Write((byte)64); // A
        }

        writer.Write(0u); // mesh flags
        writer.Write(MaterialChecksum);
        writer.Write((uint)indices.Count);
        foreach (var index in indices) writer.Write(index);
    }

    private static void WriteHierarchyObject(
        BinaryWriter writer,
        uint checksum,
        uint parentChecksum,
        short parentIndex,
        sbyte boneIndex,
        Matrix4x4 matrix)
    {
        writer.Write(checksum);
        writer.Write(parentChecksum);
        writer.Write(parentIndex);
        writer.Write(boneIndex);
        writer.Write((byte)0);
        writer.Write(0u);
        foreach (var value in new[]
                 {
                     matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                     matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                     matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                     matrix.M41, matrix.M42, matrix.M43, matrix.M44
                 })
        {
            writer.Write(value);
        }
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private sealed record CorpusExpectation(
        string Pattern,
        int Files,
        long Bytes,
        long Materials,
        long Sectors,
        long SourceVertices,
        long Meshes,
        long Indices,
        long Triangles,
        long Links);

    private sealed class MemorySource(string entryName, byte[] data) : AssetSource
    {
        public override string DisplayName => entryName;
        public override string EntryName => entryName;
        public override byte[] ReadBytes() => data;
        public override bool CompanionExists(string nameWithExtension) => false;
        public override byte[]? TryReadCompanion(string nameWithExtension) => null;
        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
