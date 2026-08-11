using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxSkinnedGeometryWriterInstanceTests
{
    [Fact]
    public void PopulatePsxSkinned_TwoInstancesKeepGeometryAndIsolateNamesAndRoots()
    {
        var file = CreateTinySuper();
        var firstRoot = Matrix4x4.CreateRotationY(0.35f)
                        * Matrix4x4.CreateTranslation(10f, 2f, -4f);
        var secondRoot = Matrix4x4.CreateRotationX(-0.25f)
                         * Matrix4x4.CreateTranslation(-7f, 5f, 12f);

        var legacyDocument = new ModelDocument { Name = "legacy" };
        Populate(legacyDocument, file);
        var legacySkeleton = Assert.Single(legacyDocument.Skeletons);
        Assert.Equal("skeleton", legacySkeleton.Name);
        Assert.Equal(Matrix4x4.Identity, legacySkeleton.RootTransform);
        Assert.Equal(["mesh_00000000", "mesh_00000001"],
            legacySkeleton.Bones.Select(static bone => bone.Name));
        Assert.Equal("combined_mesh", Assert.Single(legacyDocument.Meshes).Name);
        Assert.Equal("combined_mesh", Assert.Single(legacyDocument.Nodes).Name);

        var document = new ModelDocument { Name = "two_instances" };
        Populate(
            document, file,
            skeletonName: "traffic_000",
            rootTransform: firstRoot,
            boneNamePrefix: "traffic_000_",
            combinedMeshName: "traffic_000_mesh");
        Populate(
            document, file,
            skeletonName: "traffic_001",
            rootTransform: secondRoot,
            boneNamePrefix: "traffic_001_",
            combinedMeshName: "traffic_001_mesh");

        Assert.Equal(2, document.Skeletons.Count);
        Assert.Equal(2, document.Meshes.Count);
        Assert.Equal(2, document.Nodes.Count);

        var firstSkeleton = document.Skeletons[0];
        var secondSkeleton = document.Skeletons[1];
        Assert.Equal("traffic_000", firstSkeleton.Name);
        Assert.Equal("traffic_001", secondSkeleton.Name);
        Assert.Equal(firstRoot, firstSkeleton.RootTransform);
        Assert.Equal(secondRoot, secondSkeleton.RootTransform);
        Assert.Equal(
            ["traffic_000_mesh_00000000", "traffic_000_mesh_00000001"],
            firstSkeleton.Bones.Select(static bone => bone.Name));
        Assert.Equal(
            ["traffic_001_mesh_00000000", "traffic_001_mesh_00000001"],
            secondSkeleton.Bones.Select(static bone => bone.Name));
        Assert.Empty(firstSkeleton.Bones.Select(static bone => bone.Name)
            .Intersect(secondSkeleton.Bones.Select(static bone => bone.Name), StringComparer.Ordinal));

        for (var boneIndex = 0; boneIndex < legacySkeleton.Bones.Count; boneIndex++)
        {
            var legacyBone = legacySkeleton.Bones[boneIndex];
            var firstBone = firstSkeleton.Bones[boneIndex];
            var secondBone = secondSkeleton.Bones[boneIndex];
            Assert.Equal(legacyBone.ParentIndex, firstBone.ParentIndex);
            Assert.Equal(legacyBone.ParentIndex, secondBone.ParentIndex);
            Assert.Equal(legacyBone.LocalTransform, firstBone.LocalTransform);
            Assert.Equal(legacyBone.LocalTransform, secondBone.LocalTransform);
            Assert.Equal(legacyBone.InverseBindMatrix, firstBone.InverseBindMatrix);
            Assert.Equal(legacyBone.InverseBindMatrix, secondBone.InverseBindMatrix);
        }

        Assert.Equal(["traffic_000_mesh", "traffic_001_mesh"],
            document.Meshes.Select(static mesh => mesh.Name));
        Assert.Equal(["traffic_000_mesh", "traffic_001_mesh"],
            document.Nodes.Select(static node => node.Name));

        var firstPrimitive = Assert.Single(document.Meshes[0].Primitives);
        var secondPrimitive = Assert.Single(document.Meshes[1].Primitives);
        Assert.Equal(2, firstPrimitive.TriangleCount);
        Assert.Equal(firstPrimitive.TriangleCount, secondPrimitive.TriangleCount);
        Assert.Equal(4, document.Meshes.Sum(static mesh =>
            mesh.Primitives.Sum(static primitive => primitive.TriangleCount)));
        Assert.Equal(firstPrimitive.Vertices, secondPrimitive.Vertices);
        Assert.Equal(firstPrimitive.Indices, secondPrimitive.Indices);

        var firstSkin = Assert.IsType<ModelSkinBinding>(firstPrimitive.Skin);
        var secondSkin = Assert.IsType<ModelSkinBinding>(secondPrimitive.Skin);
        Assert.Equal(0, firstSkin.SkeletonIndex);
        Assert.Equal(1, secondSkin.SkeletonIndex);
        Assert.Equal(firstSkin.Influences, secondSkin.Influences);
        Assert.Equal(
            [0, 0, 0, 1, 1, 1],
            firstSkin.Influences.Select(static influence => influence.Joint0));
    }

    private static void Populate(
        ModelDocument document,
        PsxMeshFile file,
        string skeletonName = "skeleton",
        Matrix4x4? rootTransform = null,
        string boneNamePrefix = "",
        string combinedMeshName = "combined_mesh")
    {
        PsxSkinnedGeometryWriter.PopulatePsxSkinned(
            document,
            file,
            pshFile: null,
            textureProvider: null,
            flatSkeleton: false,
            flatBoneIndices: null,
            splineClaw: null,
            splineChains: null,
            hiddenObjectIndices: null,
            reconstructSplineAppendages: false,
            skeletonName: skeletonName,
            rootTransform: rootTransform,
            boneNamePrefix: boneNamePrefix,
            combinedMeshName: combinedMeshName);
    }

    private static PsxMeshFile CreateTinySuper()
    {
        return new PsxMeshFile
        {
            Version = 0x04,
            IsSuperModel = true,
            HasHierarchy = true,
            ScaleDivisor = 1f,
            TranslationDivisor = 1f,
            Objects =
            [
                new PsxMeshObject { MeshIndex = 0, ParentIndex = -1 },
                new PsxMeshObject
                {
                    MeshIndex = 1,
                    ParentIndex = 0,
                    RawX = 4096,
                    RawY = 8192,
                    RawZ = -4096
                }
            ],
            Meshes =
            [
                CreateTriangle(
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f)),
                CreateTriangle(
                    new Vector3(0f, 0f, 0f),
                    new Vector3(0f, 1f, 0f),
                    new Vector3(0f, 0f, 1f))
            ],
            MeshNameHashes = [0u, 0u],
            TextureHashes = []
        };
    }

    private static PsxMesh CreateTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = a.X, Y = a.Y, Z = a.Z },
                new PsxVertex { X = b.X, Y = b.Y, Z = b.Z },
                new PsxVertex { X = c.X, Y = c.Y, Z = c.Z }
            ],
            Normals = [new PsxNormal { Z = 1f }],
            Faces =
            [
                new PsxFace
                {
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    NormalIndex = 0,
                    R = 255,
                    G = 255,
                    B = 255
                }
            ],
            VertexCount = 3,
            LodNextMeshIndex = ushort.MaxValue
        };
    }
}
