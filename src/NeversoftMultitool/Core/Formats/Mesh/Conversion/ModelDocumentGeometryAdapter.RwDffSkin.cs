using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    // Row-vector convention: vertex' = vertex * R rotates Z-up RW data to Y-up glTF.
    private static readonly Matrix4x4 RwDffZupToYupRotation = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private static void PopulateRwDffSkinned(
        ModelDocument document,
        RwDffClump clump,
        RwSkinData skinRef,
        Dictionary<(int Geometry, int Material), int> materialMap)
    {
        var skeletonIndex = document.Skeletons.Count;
        document.Skeletons.Add(BuildRwDffSkeleton(skinRef));

        var mesh = new ModelMesh { Name = "skinned_mesh" };
        foreach (var atomic in clump.Atomics)
        {
            if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                continue;

            var geometry = clump.Geometries[atomic.GeometryIndex];
            if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                continue;

            var skin = atomic.SkinData ?? skinRef;
            foreach (var group in geometry.Triangles.GroupBy(static tri => tri.MaterialIndex))
                AddRwDffSkinnedPrimitive(document, mesh, geometry, skin, atomic.GeometryIndex,
                    group.Key, group, materialMap, skeletonIndex);
        }

        AddMeshNode(document, "skinned_mesh", mesh);
    }

    private static void AddRwDffSkinnedPrimitive(
        ModelDocument document,
        ModelMesh mesh,
        RwGeometry geometry,
        RwSkinData skin,
        int geometryIndex,
        int materialKey,
        IEnumerable<RwTriangle> triangles,
        Dictionary<(int Geometry, int Material), int> materialMap,
        int skeletonIndex)
    {
        var materialIndex = materialMap.TryGetValue((geometryIndex, materialKey), out var mapped)
            ? mapped
            : AddMaterial(document, new RenderMaterial { Name = "__default__" });

        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var influences = new List<ModelBoneInfluences>();
        foreach (var tri in triangles)
        {
            AddSkinnedTriangle(
                vertices, indices, influences,
                MakeRwVertex(geometry, tri.V0), MakeRwSkinInfluence(skin, tri.V0),
                MakeRwVertex(geometry, tri.V1), MakeRwSkinInfluence(skin, tri.V1),
                MakeRwVertex(geometry, tri.V2), MakeRwSkinInfluence(skin, tri.V2));
        }

        if (indices.Count == 0)
            return;

        AddPrimitive(mesh, $"mat_{materialKey:D3}", materialIndex, vertices, indices,
            new ModelSkinBinding
            {
                SkeletonIndex = skeletonIndex,
                Influences = influences.ToArray()
            });
    }

    private static ModelSkeleton BuildRwDffSkeleton(RwSkinData skin)
    {
        var parentIndex = ReconstructRwDffParentChain(skin);

        var globalBind = new Matrix4x4[skin.NumBones];
        for (var i = 0; i < skin.NumBones; i++)
        {
            if (!Matrix4x4.Invert(skin.Bones[i].InverseBindMatrix, out var bind))
                bind = Matrix4x4.Identity;
            globalBind[i] = bind;
        }

        var skeleton = new ModelSkeleton { Name = "skeleton" };
        for (var i = 0; i < skin.NumBones; i++)
        {
            var parent = parentIndex[i];
            Matrix4x4 local;
            if (parent >= 0 && parent < i)
            {
                local = Matrix4x4.Invert(globalBind[parent], out var invParent)
                    ? globalBind[i] * invParent
                    : globalBind[i];
            }
            else
            {
                // Root bones: pre-multiply by Z-up → Y-up rotation so the whole skeleton
                // (and thus the skinned mesh) is rotated at bind pose without touching IBMs.
                local = globalBind[i] * RwDffZupToYupRotation;
            }

            skeleton.Bones.Add(new ModelBone
            {
                Name = $"bone_{i}",
                ParentIndex = parent,
                LocalTransform = SanitizeAffine(local),
                InverseBindMatrix = skin.Bones[i].InverseBindMatrix,
                NativeChecksum = (uint)skin.Bones[i].Id
            });
        }

        return skeleton;
    }

    private static int[] ReconstructRwDffParentChain(RwSkinData skin)
    {
        // HAnim DFS encoding (matches legacy RwDffGltfWriter.BuildBoneHierarchy):
        //   bit 1 (PUSH) = save parent before descending,
        //   bit 0 (POP)  = restore parent after this bone.
        var parentIndex = new int[skin.NumBones];
        var parentStack = new Stack<int>();
        var currentParent = -1;
        for (var i = 0; i < skin.NumBones; i++)
        {
            parentIndex[i] = currentParent;
            var flags = skin.Bones[i].Flags & 0x03;
            if ((flags & 0x02) != 0)
                parentStack.Push(currentParent);
            currentParent = i;
            if ((flags & 0x01) != 0)
                currentParent = parentStack.Count > 0 ? parentStack.Pop() : -1;
        }

        return parentIndex;
    }

    private static ModelBoneInfluences MakeRwSkinInfluence(RwSkinData skin, int vertexIndex)
    {
        var baseIdx = vertexIndex * 4;
        if (baseIdx + 3 >= skin.BoneIndices.Length)
            return ModelBoneInfluences.Single(0);

        return new ModelBoneInfluences(
            skin.BoneIndices[baseIdx],
            skin.BoneIndices[baseIdx + 1],
            skin.BoneIndices[baseIdx + 2],
            skin.BoneIndices[baseIdx + 3],
            skin.BoneWeights[baseIdx],
            skin.BoneWeights[baseIdx + 1],
            skin.BoneWeights[baseIdx + 2],
            skin.BoneWeights[baseIdx + 3]);
    }

    private static Matrix4x4 SanitizeAffine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }
}
