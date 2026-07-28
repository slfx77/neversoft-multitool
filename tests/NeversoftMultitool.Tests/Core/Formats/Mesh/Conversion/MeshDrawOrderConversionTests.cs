using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Guards for the DDM/PSX conversion from baked coplanar-layer vertex
///     offsets to draw-order metadata (the worldzone B1 pattern): vertices
///     export at AUTHORED positions, ranked decal splits / detected opaque
///     overlays become their own metadata-carrying meshes, and the PSX
///     semi-transparent lift (deliberately retained — non-rigid, and ST-on-ST
///     order needs the OT tie-break direction pinned first) stays intact.
/// </summary>
public sealed class MeshDrawOrderConversionTests
{
    // ---------------------------------------------------------------- DDM

    [Fact]
    public void DdmPlacedLevel_SplitsRankedDecalsIntoPassMeshes_WithoutDisplacement()
    {
        var ddm = CreateDdmFile(baseDrawOrder: 0, decalDrawOrder: 5);
        var document = new ModelDocument { Name = "ddm_test", SourceKind = ModelSourceKind.DdmPlacedLevel };
        DdmGeometryWriter.PopulateDdmPlacedLevel(document, ddm, null, null, null, null);

        Assert.Equal(2, document.Meshes.Count);
        var baseMesh = Assert.Single(document.Meshes, static m => m.Name == "ddm_quad");
        var passMesh = Assert.Single(document.Meshes, static m => m.Name == "ddm_quad__pass1");

        // The decal split carries draw-order metadata whose separation vector
        // equals the old bake (rank 1 x 0.1 along the split's average normal,
        // +Z in glTF space for the fixture's (0,0,1) normals).
        Assert.Empty(baseMesh.Primitives.Single().NativeMetadata.OfType<MeshDrawOrderMetadata>());
        var metadata = Assert.Single(
            passMesh.Primitives.Single().NativeMetadata.OfType<MeshDrawOrderMetadata>());
        Assert.Equal(1, metadata.DrawIndex);
        Assert.Equal(1, metadata.PassIndex);
        Assert.Equal(0f, metadata.BlendOffsetX, 6);
        Assert.Equal(0f, metadata.BlendOffsetY, 6);
        Assert.Equal(0.1f, metadata.BlendOffsetZ, 6);

        // No displacement anywhere: every emitted vertex must sit on the
        // authored Z = 0 plane (the old writer pushed decal verts to Z = 0.1).
        var allVertices = document.Meshes
            .SelectMany(static m => m.Primitives)
            .SelectMany(static p => p.Vertices);
        Assert.All(allVertices, static vertex => Assert.Equal(0f, vertex.Position.Z, 6));
    }

    [Fact]
    public void DdmPlacedLevel_FlatObjectDecalsLadderToPassOne()
    {
        // Flat objects (min half-extent < 1.5) used to get the unconditional
        // 0.1 lift even at rank 0 — they now ladder to pass 1 instead.
        var ddm = CreateDdmFile(baseDrawOrder: 0, decalDrawOrder: 0, extentZ: 0.5f);
        var document = new ModelDocument { Name = "ddm_flat", SourceKind = ModelSourceKind.DdmPlacedLevel };
        DdmGeometryWriter.PopulateDdmPlacedLevel(document, ddm, null, null, null, null);

        var passMesh = Assert.Single(document.Meshes, static m => m.Name == "ddm_quad__pass1");
        Assert.Equal(2, passMesh.Primitives.Count);
        Assert.DoesNotContain(document.Meshes, static m => m.Name == "ddm_quad");
    }

    // ---------------------------------------------------------------- PSX

    [Fact]
    public void Psx_SplitsOpaqueOverlayIntoMetadataMesh_AtAuthoredPositions()
    {
        var file = CreatePsxFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0f, 2));
        var document = new ModelDocument { Name = "psx_test", SourceKind = ModelSourceKind.Psx };
        PsxGeometryWriter.PopulatePsx(document, file, null);

        // Object 1's single face is the detected overlay: it becomes its own
        // node/mesh with the __overlay suffix; object 0 keeps its plain name.
        Assert.Contains(document.Nodes, static n => n.Name == "object_000");
        var overlayNode = Assert.Single(document.Nodes, static n => n.Name.Contains("__overlay"));
        var overlayMesh = document.Meshes[overlayNode.MeshIndex!.Value];

        var metadata = Assert.Single(
            overlayMesh.Primitives.Single().NativeMetadata.OfType<MeshDrawOrderMetadata>());
        Assert.Equal(1, metadata.DrawIndex);
        Assert.Equal(1, metadata.PassIndex);

        // Authored plane is y = 0; the old writer lifted overlay verts 0.25
        // along the plane normal. The separation vector now ships as metadata
        // only (magnitude 0.25 along +/-Y).
        foreach (var vertex in overlayMesh.Primitives.Single().Vertices)
            Assert.Equal(0f, vertex.Position.Y, 6);
        var offset = new Vector3(metadata.BlendOffsetX, metadata.BlendOffsetY, metadata.BlendOffsetZ);
        Assert.Equal(0.25f, offset.Length(), 5);
        Assert.Equal(0.25f, MathF.Abs(offset.Y), 5);
    }

    [Fact]
    public void Psx_SemiTransparentFacesKeepTheirLift()
    {
        var file = CreatePsxFile(CreateQuad(4f, 0f, 3, semiTransparent: true));
        var document = new ModelDocument { Name = "psx_st", SourceKind = ModelSourceKind.Psx };
        PsxGeometryWriter.PopulatePsx(document, file, null);

        // The ST lift is deliberately unchanged: every vertex sits 0.25 glTF
        // units off the authored y = 0 plane along the face normal.
        var mesh = document.Meshes.Single();
        Assert.DoesNotContain(document.Nodes, static n => n.Name.Contains("__overlay"));
        foreach (var vertex in mesh.Primitives.Single().Vertices)
            Assert.Equal(0.25f, MathF.Abs(vertex.Position.Y), 5);
    }

    [Fact]
    public void ToDictionary_SerializesGenericDrawOrderKind()
    {
        var dictionary = BlendPackageManifest.ToDictionary(
            new MeshDrawOrderMetadata(1, 1, 7, 0f, 0.25f, 0f));

        Assert.Equal("draw_order", dictionary["kind"]);
        Assert.Equal(1, dictionary["drawIndex"]);
        Assert.Equal(1, dictionary["passIndex"]);
        Assert.Equal(7, dictionary["overlapGroup"]);
        Assert.Equal([0f, 0.25f, 0f], Assert.IsType<float[]>(dictionary["blendOffset"]));
    }

    // ---------------------------------------------------------------- fixtures

    private static DdmFile CreateDdmFile(uint baseDrawOrder, uint decalDrawOrder, float extentZ = 2f)
    {
        return new DdmFile
        {
            Objects =
            [
                new DdmObject
                {
                    Name = "ddm_quad",
                    Checksum = 0xDEADBEAF,
                    BBoxExtentX = 2f,
                    BBoxExtentY = 2f,
                    BBoxExtentZ = extentZ,
                    Materials =
                    [
                        MakeDdmMaterial("base", baseDrawOrder),
                        MakeDdmMaterial("decal", decalDrawOrder)
                    ],
                    Vertices =
                    [
                        MakeDdmVertex(0f, 0f), MakeDdmVertex(1f, 0f), MakeDdmVertex(0f, 1f), MakeDdmVertex(1f, 1f),
                        MakeDdmVertex(0.25f, 0.25f), MakeDdmVertex(0.75f, 0.25f), MakeDdmVertex(0.25f, 0.75f),
                        MakeDdmVertex(0.75f, 0.75f)
                    ],
                    Indices = [0, 1, 2, 3, 4, 5, 6, 7],
                    Splits = [new DdmSplit(0, 0, 4), new DdmSplit(1, 4, 4)]
                }
            ]
        };
    }

    private static DdmMaterial MakeDdmMaterial(string name, uint drawOrder)
    {
        return new DdmMaterial
        {
            Name = name,
            TextureName = "No_Texture_Map",
            DrawOrder = drawOrder,
            DiffuseR = 255,
            DiffuseG = 255,
            DiffuseB = 255,
            DiffuseA = 255
        };
    }

    private static DdmVertex MakeDdmVertex(float x, float y)
    {
        // Normal (0,0,1) in DDM space maps to +Z in glTF space (-NX,-NY,NZ).
        return new DdmVertex(x, y, 0f, 0f, 0f, 1f, 255, 255, 255, 255, x, y);
    }

    private static PsxMeshFile CreatePsxFile(params PsxMesh[] meshes)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = meshes.Select((_, index) => new PsxMeshObject { MeshIndex = (ushort)index }).ToList(),
            Meshes = meshes.ToList(),
            MeshNameHashes = new uint[meshes.Length],
            TextureHashes = [1, 2, 3],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMesh CreateQuad(float size, float y, uint textureHash, bool semiTransparent = false)
    {
        var half = size * 0.5f;
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = -half, Y = y, Z = -half },
                new PsxVertex { X = half, Y = y, Z = -half },
                new PsxVertex { X = -half, Y = y, Z = half },
                new PsxVertex { X = half, Y = y, Z = half }
            ],
            Normals = [new PsxNormal { Y = 1f }],
            Faces =
            [
                new PsxFace
                {
                    IsQuad = true,
                    IsTextured = true,
                    IsSemiTransparent = semiTransparent,
                    TextureHash = textureHash,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    Index3 = 3
                }
            ]
        };
    }
}
