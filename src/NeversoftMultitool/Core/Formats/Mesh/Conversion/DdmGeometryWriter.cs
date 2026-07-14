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
///     Xbox DDM meshes and PSX-layout-placed DDM levels: split strips, draw-order
///     decal offsets, and DDX texture materials.
/// </summary>
internal static class DdmGeometryWriter
{
    private const float DdmDecalNormalOffset = 0.1f;

    public static void PopulateDdm(
        ModelDocument document,
        DdmFile ddm,
        Dictionary<string, byte[]>? ddxTextures,
        List<string>? textureDirs = null)
    {
        textureDirs ??= [];
        var materialBase = 0;
        foreach (var obj in ddm.Objects)
        {
            var mesh = new ModelMesh { Name = obj.Name };
            for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
            {
                var split = obj.Splits[splitIndex];
                if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                    continue;

                var material = obj.Materials[split.MaterialIndex];
                var materialIndex = materialBase + split.MaterialIndex;
                if (materialIndex >= 0 && materialIndex < document.Materials.Count)
                    ApplyDdmMaterial(document, document.Materials[materialIndex], material, ddxTextures, textureDirs);

                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                var end = Math.Min(obj.Indices.Length, split.IndexOffset + split.IndexCount);
                for (var i = split.IndexOffset; i + 2 < end; i++)
                {
                    var ai = obj.Indices[i];
                    var bi = obj.Indices[i + 1];
                    var ci = obj.Indices[i + 2];
                    if (ai == bi || ai == ci || bi == ci ||
                        ai >= obj.Vertices.Count ||
                        bi >= obj.Vertices.Count ||
                        ci >= obj.Vertices.Count)
                    {
                        continue;
                    }

                    var va = MakeDdmVertex(obj.Vertices[ai]);
                    var vb = MakeDdmVertex(obj.Vertices[bi]);
                    var vc = MakeDdmVertex(obj.Vertices[ci]);
                    if ((i - split.IndexOffset) % 2 == 0)
                        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, va, vb, vc);
                    else
                        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, vb, va, vc);
                }

                ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"split_{splitIndex:D3}", materialIndex, vertices, indices);
            }

            ModelDocumentGeometryAdapter.AddMeshNode(document, obj.Name, mesh);
            materialBase += obj.Materials.Count;
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    public static void PopulateDdmPlacedLevel(
        ModelDocument document,
        DdmFile levelDdm,
        PsxLayoutFile? levelPsx,
        DdmFile? objectsDdm,
        PsxLayoutFile? objectsPsx,
        Dictionary<string, byte[]>? ddxTextures,
        List<string>? textureDirs = null)
    {
        textureDirs ??= [];
        PopulateDdmWithLayout(document, levelDdm, levelPsx, ddxTextures, textureDirs, "level");
        if (objectsDdm != null)
            PopulateDdmWithLayout(document, objectsDdm, objectsPsx, ddxTextures, textureDirs, "objects");

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }
    private static void ApplyDdmMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        DdmMaterial material,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        renderMaterial.BaseColor = new Vector4(
            material.DiffuseR / 255f,
            material.DiffuseG / 255f,
            material.DiffuseB / 255f,
            material.DiffuseA / 255f);

        var isAdditive = material.BlendMode is 1 or 3;
        if (!material.TextureName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        {
            var loaded = MeshTextureHelper.LoadTexture(textureDirs, material.TextureName, ddxTextures);
            if (loaded != null)
            {
                var pngBytes = isAdditive
                    ? MeshTextureHelper.ConvertLuminanceToAlpha(loaded.Value.Bytes)
                    : loaded.Value.Bytes;
                renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(document, material.TextureName, pngBytes);
                if (isAdditive || loaded.Value.HasAlpha)
                    renderMaterial.AlphaMode = ModelAlphaMode.Blend;
                else if (material.BlendMode == 2)
                    renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                else
                    renderMaterial.AlphaMode = ModelAlphaMode.Opaque;
            }
        }

        if (isAdditive)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (material.BlendMode == 2)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }
    private static void PopulateDdmWithLayout(
        ModelDocument document,
        DdmFile ddm,
        PsxLayoutFile? psx,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs,
        string nodePrefix)
    {
        if (psx == null)
        {
            for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
            {
                var obj = ddm.Objects[objectIndex];
                var mesh = BuildDdmObjectMesh(document, obj, ddxTextures, textureDirs);
                ModelDocumentGeometryAdapter.AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
            }

            return;
        }

        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshSlotToDdm = DdmHashLookup.ResolveMeshIndices(psx, ddmByHash);
        var placedIndices = new HashSet<int>();
        var meshCache = new Dictionary<int, int>();

        foreach (var psxObj in psx.Objects)
        {
            if (!meshSlotToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIndex) ||
                (uint)ddmIndex >= (uint)ddm.Objects.Count)
            {
                continue;
            }

            placedIndices.Add(ddmIndex);
            if (!meshCache.TryGetValue(ddmIndex, out var meshIndex))
            {
                var mesh = BuildDdmObjectMesh(document, ddm.Objects[ddmIndex], ddxTextures, textureDirs);
                var addedIndex = ModelDocumentGeometryAdapter.AddMesh(document, mesh);
                if (!addedIndex.HasValue)
                    continue;

                meshIndex = addedIndex.Value;
                meshCache[ddmIndex] = meshIndex;
            }

            ModelDocumentGeometryAdapter.AddMeshNode(
                document,
                $"{nodePrefix}_{ddm.Objects[ddmIndex].Name}_{psxObj.MeshIndex:D4}",
                meshIndex,
                Matrix4x4.CreateTranslation(new Vector3(-psxObj.X, -psxObj.Y, psxObj.Z)));
        }

        for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
        {
            if (placedIndices.Contains(objectIndex))
                continue;

            var obj = ddm.Objects[objectIndex];
            var mesh = BuildDdmObjectMesh(document, obj, ddxTextures, textureDirs);
            ModelDocumentGeometryAdapter.AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
        }
    }

    private static ModelMesh BuildDdmObjectMesh(
        ModelDocument document,
        DdmObject obj,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        var mesh = new ModelMesh { Name = obj.Name };
        if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
            return mesh;

        var materialIndices = AddDdmObjectMaterials(document, obj, ddxTextures, textureDirs);
        var minExtent = Math.Min(obj.BBoxExtentX, Math.Min(obj.BBoxExtentY, obj.BBoxExtentZ));
        var isFlat = minExtent < 1.5f;
        var drawOrderRanks = BuildDdmDrawOrderRanks(obj);

        for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
        {
            var split = obj.Splits[splitIndex];
            if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                continue;

            var mat = obj.Materials[split.MaterialIndex];
            var rank = drawOrderRanks.GetValueOrDefault(mat.DrawOrder);
            var drawOrderOffset = rank * DdmDecalNormalOffset;
            var materialOffset = isFlat || mat.BlendMode != 0 ? DdmDecalNormalOffset : 0f;
            var normalOffset = Math.Max(drawOrderOffset, materialOffset);

            AddDdmStripPrimitive(
                mesh,
                $"split_{splitIndex:D3}",
                materialIndices[split.MaterialIndex],
                obj,
                split,
                normalOffset);
        }

        return mesh;
    }

    private static int[] AddDdmObjectMaterials(
        ModelDocument document,
        DdmObject obj,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs)
    {
        var materialIndices = new int[obj.Materials.Count];
        for (var i = 0; i < obj.Materials.Count; i++)
        {
            var material = obj.Materials[i];
            var renderMaterial = new RenderMaterial { Name = material.Name };
            renderMaterial.NativeMetadata.Add(new DdmBlendRenderMetadata(
                material.BlendMode,
                material.DrawOrder,
                material.TextureName,
                material.DiffuseR,
                material.DiffuseG,
                material.DiffuseB,
                material.DiffuseA));
            ApplyDdmMaterial(document, renderMaterial, material, ddxTextures, textureDirs);
            materialIndices[i] = ModelDocumentGeometryAdapter.AddMaterial(document, renderMaterial);
        }

        return materialIndices;
    }

    private static void AddDdmStripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        DdmObject obj,
        DdmSplit split,
        float normalOffset)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var end = Math.Min(obj.Indices.Length, split.IndexOffset + split.IndexCount);

        for (var i = split.IndexOffset; i + 2 < end; i++)
        {
            var ai = obj.Indices[i];
            var bi = obj.Indices[i + 1];
            var ci = obj.Indices[i + 2];
            if (ai == bi || ai == ci || bi == ci ||
                ai >= obj.Vertices.Count ||
                bi >= obj.Vertices.Count ||
                ci >= obj.Vertices.Count)
            {
                continue;
            }

            var va = MakeDdmVertex(obj.Vertices[ai], normalOffset);
            var vb = MakeDdmVertex(obj.Vertices[bi], normalOffset);
            var vc = MakeDdmVertex(obj.Vertices[ci], normalOffset);
            if ((i - split.IndexOffset) % 2 == 0)
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, va, vb, vc);
            else
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, vb, va, vc);
        }

        ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices);
    }

    private static Dictionary<uint, int> BuildDdmDrawOrderRanks(DdmObject obj)
    {
        var ranks = new Dictionary<uint, int>();
        foreach (var drawOrder in obj.Splits
                     .Select(split => split.MaterialIndex)
                     .Where(materialIndex => materialIndex < obj.Materials.Count)
                     .Select(materialIndex => obj.Materials[materialIndex].DrawOrder)
                     .Distinct()
                     .Order())
        {
            ranks.Add(drawOrder, ranks.Count);
        }

        return ranks;
    }

    private static ModelVertex MakeDdmVertex(DdmVertex vertex, float normalOffset = 0f)
    {
        var normal = ModelDocumentGeometryAdapter.NormalizeOrDefault(new Vector3(-vertex.NX, -vertex.NY, vertex.NZ));
        var position = new Vector3(-vertex.X, -vertex.Y, vertex.Z);
        if (normalOffset > 0f)
            position += normal * normalOffset;

        return new ModelVertex(
            position,
            normal,
            new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f),
            new Vector2(vertex.U, vertex.V));
    }
}
