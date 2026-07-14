using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     RenderWare DFF clumps: frame-hierarchy skeletons, HAnim skinning, and
///     RW material/texture emission (shared with the BSP writer).
/// </summary>
internal static class RwGeometryWriter
{
    public static void PopulateRwDff(
        ModelDocument document,
        RwDffClump clump,
        MeshNamedTextureResolver? textureProvider)
    {
        var materialMap = new Dictionary<(int Geometry, int Material), int>();
        for (var geometryIndex = 0; geometryIndex < clump.Geometries.Length; geometryIndex++)
        {
            var geometry = clump.Geometries[geometryIndex];
            for (var materialIndex = 0; materialIndex < geometry.Materials.Length; materialIndex++)
            {
                materialMap[(geometryIndex, materialIndex)] =
                    AddRwMaterial(document, geometry.Materials[materialIndex], textureProvider, false);
            }
        }

        var skinnedAtomic = clump.Atomics.FirstOrDefault(a => a.SkinData != null);
        if (skinnedAtomic?.SkinData != null)
        {
            PopulateRwDffSkinned(document, clump, skinnedAtomic.SkinData, materialMap);
            ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
            return;
        }

        var frameWorld = BuildRwFrameWorldTransforms(clump.Frames);
        foreach (var atomic in clump.Atomics)
        {
            if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                continue;

            var geometry = clump.Geometries[atomic.GeometryIndex];
            if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                continue;

            var mesh = new ModelMesh { Name = $"geom_{atomic.GeometryIndex}" };
            foreach (var group in geometry.Triangles.GroupBy(static tri => tri.MaterialIndex))
            {
                var materialIndex = materialMap.TryGetValue((atomic.GeometryIndex, group.Key), out var mapped)
                    ? mapped
                    : ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial { Name = "__default__" });
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                foreach (var tri in group)
                {
                    ModelDocumentGeometryAdapter.AddTriangle(
                        vertices,
                        indices,
                        MakeRwVertex(geometry, tri.V0),
                        MakeRwVertex(geometry, tri.V1),
                        MakeRwVertex(geometry, tri.V2));
                }

                ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{group.Key:D3}", materialIndex, vertices, indices);
            }

            var transform = atomic.FrameIndex >= 0 && atomic.FrameIndex < frameWorld.Length
                ? frameWorld[atomic.FrameIndex]
                : Matrix4x4.Identity;
            ModelDocumentGeometryAdapter.AddMeshNode(document, $"atomic_{atomic.GeometryIndex}", mesh, transform);
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static Matrix4x4[] BuildRwFrameWorldTransforms(RwFrame[] frames)
    {
        var world = new Matrix4x4[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            var local = frames[i].LocalTransform;
            world[i] = frames[i].ParentIndex >= 0 && frames[i].ParentIndex < i
                ? local * world[frames[i].ParentIndex]
                : local;
        }

        return world;
    }

    internal static Vector4 ToColor(RwVertexColor color)
    {
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    internal static bool IsRwDevTexture(string name)
    {
        return string.Equals(name, "wire", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "transparent", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_Collision_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_TriggerPlane_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_CR_VertPoly", StringComparison.OrdinalIgnoreCase);
    }

    internal static int AddRwMaterial(
        ModelDocument document,
        RwMaterial material,
        MeshNamedTextureResolver? textureProvider,
        bool forBsp)
    {
        var renderMaterial = new RenderMaterial
        {
            Name = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}"
        };
        renderMaterial.NativeMetadata.Add(new RwGsAlphaRenderMetadata(
            material.GsAlpha,
            material.GsAlphaFix,
            material.IsAdditive,
            material.IsSubtractive,
            material.IsBlend,
            material.TextureName));
        ApplyRwMaterial(document, renderMaterial, material, textureProvider, forBsp);
        return ModelDocumentGeometryAdapter.AddMaterial(document, renderMaterial);
    }

    internal static void ApplyRwMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        RwMaterial material,
        MeshNamedTextureResolver? textureProvider,
        bool forBsp)
    {
        renderMaterial.BaseColor = new Vector4(
            material.R / 255f,
            material.G / 255f,
            material.B / 255f,
            material.A / 255f);

        var textureHasAlpha = false;
        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                if (forBsp && material.IsAdditive)
                {
                    pngBytes = MeshTextureHelper.ConvertBlendTexture(pngBytes, 255, 255, 255);
                    textureHasAlpha = true;
                }
                else if (forBsp && material.IsSubtractive)
                {
                    pngBytes = MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0);
                    textureHasAlpha = true;
                }
                else if (forBsp)
                {
                    (pngBytes, textureHasAlpha) = MeshTextureHelper.ApplyColorKey(pngBytes);
                }

                renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(document, material.TextureName, pngBytes);
            }
        }

        if (material.A < 255 || material.IsBlend)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (textureHasAlpha)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }
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

        ModelDocumentGeometryAdapter.AddMeshNode(document, "skinned_mesh", mesh);
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
            : ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial { Name = "__default__" });

        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var influences = new List<ModelBoneInfluences>();
        foreach (var tri in triangles)
        {
            ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                vertices, indices, influences,
                MakeRwVertex(geometry, tri.V0), MakeRwSkinInfluence(skin, tri.V0),
                MakeRwVertex(geometry, tri.V1), MakeRwSkinInfluence(skin, tri.V1),
                MakeRwVertex(geometry, tri.V2), MakeRwSkinInfluence(skin, tri.V2));
        }

        if (indices.Count == 0)
            return;

        ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"mat_{materialKey:D3}", materialIndex, vertices, indices,
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

    private static ModelVertex MakeRwVertex(RwGeometry geometry, int index)
    {
        var position = index < geometry.Vertices.Length ? geometry.Vertices[index] : Vector3.Zero;
        var normal = geometry.Normals != null && index < geometry.Normals.Length
            ? ModelDocumentGeometryAdapter.NormalizeOrDefault(geometry.Normals[index])
            : Vector3.UnitY;
        var color = geometry.Colors != null && index < geometry.Colors.Length
            ? ToColor(geometry.Colors[index])
            : Vector4.One;
        var uv = geometry.UVs != null && index < geometry.UVs.Length ? geometry.UVs[index] : Vector2.Zero;
        return new ModelVertex(position, normal, color, uv);
    }
}
