using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class GltfCollisionGroupExtrasTests
{
    [Fact]
    public void BuildGlbBytes_PsxInlineCollision_PreservesOrderedRawGroupsAfterMaterialMerge()
    {
        var document = new ModelDocument
        {
            Name = "psx_inline_collision",
            SourceKind = ModelSourceKind.Psx
        };
        Assert.Equal(3, PsxInlineCollisionGeometryWriter.PopulateOverlay(document, CreatePsxLevel()));

        using var json = ExportJson(document, expectedTriangles: 3);
        var mesh = Assert.Single(json.RootElement.GetProperty("meshes").EnumerateArray());

        // The collision writer deliberately shares one material. Pin the
        // SharpGLTF merge that makes the mesh-level range table necessary.
        AssertMergedIndexCount(json.RootElement, mesh, expectedTriangles: 3);
        var groups = mesh.GetProperty("extras")
            .GetProperty("neversoftCollisionGroups");
        Assert.Collection(
            groups.EnumerateArray(),
            group => AssertGroup(group, 0, 1, 0x1234, loaderInvisible: false),
            group => AssertGroup(group, 1, 2, 0xBEEF, loaderInvisible: true));
    }

    [Fact]
    public void BuildGlbBytes_RwBspInlineCollision_PreservesOrderedRawGroupsAfterMaterialMerge()
    {
        var document = new ModelDocument
        {
            Name = "rw_bsp_inline_collision",
            SourceKind = ModelSourceKind.RenderWareBsp
        };
        Assert.Equal(3, RwBspCollisionGeometryWriter.PopulateOverlay(document, CreateRwWorld()));

        using var json = ExportJson(document, expectedTriangles: 3);
        var mesh = Assert.Single(json.RootElement.GetProperty("meshes").EnumerateArray());

        AssertMergedIndexCount(json.RootElement, mesh, expectedTriangles: 3);
        var groups = mesh.GetProperty("extras")
            .GetProperty("neversoftCollisionGroups");
        Assert.Collection(
            groups.EnumerateArray(),
            group => AssertGroup(group, 0, 1, 0x0000),
            group => AssertGroup(group, 1, 2, 0xFFFF));
    }

    private static void AssertGroup(
        JsonElement group,
        int triangleStart,
        int triangleCount,
        int collisionFlags,
        bool? loaderInvisible = null)
    {
        Assert.Equal(triangleStart, group.GetProperty("triangleStart").GetInt32());
        Assert.Equal(triangleCount, group.GetProperty("triangleCount").GetInt32());
        Assert.Equal(collisionFlags, group.GetProperty("collisionFlags").GetInt32());
        if (loaderInvisible.HasValue)
        {
            Assert.Equal(
                loaderInvisible.Value,
                group.GetProperty("loaderInvisible").GetBoolean());
        }
        else
        {
            Assert.False(group.TryGetProperty("loaderInvisible", out _));
        }
    }

    private static void AssertMergedIndexCount(
        JsonElement root,
        JsonElement mesh,
        int expectedTriangles)
    {
        var primitive = Assert.Single(mesh.GetProperty("primitives").EnumerateArray());
        var accessorIndex = primitive.GetProperty("indices").GetInt32();
        Assert.Equal(
            expectedTriangles * 3,
            root.GetProperty("accessors")[accessorIndex].GetProperty("count").GetInt32());
    }

    private static JsonDocument ExportJson(ModelDocument document, int expectedTriangles)
    {
        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(expectedTriangles, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, writable: false);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(checked((uint)glbBytes.Length), reader.ReadUInt32());
        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        return JsonDocument.Parse(reader.ReadBytes(checked((int)jsonLength)));
    }

    private static PsxMeshFile CreatePsxLevel()
    {
        var mesh = new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                new PsxVertex { X = 1f, Y = 0f, Z = 0f },
                new PsxVertex { X = 0f, Y = 1f, Z = 0f },
                new PsxVertex { X = 1f, Y = 1f, Z = 0f }
            ],
            Normals = [],
            Faces =
            [
                new PsxFace
                {
                    CollisionFlags = 0x1234,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2
                }
            ],
            InvisibleFaces =
            [
                new PsxFace
                {
                    Flags = 0x0080,
                    CollisionFlags = 0xBEEF,
                    IsQuad = true,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    Index3 = 3
                }
            ],
            FaceReadInfos =
            [
                CreateAcceptedFaceReadInfo(0, 0x0000),
                CreateAcceptedFaceReadInfo(1, 0x0080)
            ]
        };
        return new PsxMeshFile
        {
            Version = 0x06,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes = [mesh],
            MeshNameHashes = [0],
            TextureHashes = [],
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };
    }

    private static PsxFaceReadInfo CreateAcceptedFaceReadInfo(
        int rawFaceIndex,
        ushort flags) =>
        new()
        {
            RawFaceIndex = rawFaceIndex,
            Offset = 0,
            Flags = flags,
            Length = 20,
            BytesConsumed = 20,
            UnderreadBytes = 0,
            OverreadBytes = 0,
            IsLengthAligned = true,
            IsAccepted = true,
            AcceptedFaceIndex = rawFaceIndex
        };

    private static RwBspWorld CreateRwWorld()
    {
        return new RwBspWorld
        {
            FormatFlags = 0,
            TotalTriangles = 3,
            TotalVertices = 5,
            Materials = [],
            Sections =
            [
                new RwBspSection
                {
                    MatListWindowBase = 0,
                    Vertices =
                    [
                        Vector3.Zero,
                        Vector3.UnitX,
                        Vector3.UnitY,
                        Vector3.One,
                        new Vector3(2f, 0f, 0f)
                    ],
                    Normals = null,
                    Colors = null,
                    UVs = null,
                    Triangles =
                    [
                        new RwTriangle(0, 1, 2, 0),
                        new RwTriangle(1, 3, 2, 0),
                        new RwTriangle(1, 4, 3, 0)
                    ],
                    TriangleCollisionFlags = [0xFFFF, 0x0000, 0xFFFF]
                }
            ]
        };
    }
}
