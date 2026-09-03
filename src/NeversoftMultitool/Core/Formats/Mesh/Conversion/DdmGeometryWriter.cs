using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

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

                ModelDocumentGeometryAdapter.AddPrimitive(mesh, $"split_{splitIndex:D3}", materialIndex, vertices,
                    indices);
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
        List<string>? textureDirs = null,
        DdmSkyClassifier.Result? objectSky = null)
    {
        textureDirs ??= [];
        PopulateDdmWithLayout(document, levelDdm, levelPsx, ddxTextures, textureDirs, "level");
        if (objectsDdm != null)
            PopulateDdmWithLayout(
                document, objectsDdm, objectsPsx, ddxTextures, textureDirs, "objects", objectSky);

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
                renderMaterial.TextureIndex ??=
                    AddDdmTexture(document, material.TextureName, pngBytes);
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

    private static int AddDdmTexture(ModelDocument document, string name, byte[] pngBytes)
    {
        for (var i = 0; i < document.Textures.Count; i++)
        {
            var texture = document.Textures[i];
            if (string.Equals(texture.Name, name, StringComparison.OrdinalIgnoreCase) &&
                texture.PngBytes is { } existingBytes &&
                existingBytes.AsSpan().SequenceEqual(pngBytes))
            {
                return i;
            }
        }

        document.Textures.Add(new ModelTexture
        {
            Name = name,
            PngBytes = pngBytes
        });
        return document.Textures.Count - 1;
    }

    private static void PopulateDdmWithLayout(
        ModelDocument document,
        DdmFile ddm,
        PsxLayoutFile? psx,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs,
        string nodePrefix,
        DdmSkyClassifier.Result? sky = null)
    {
        if (psx == null)
        {
            for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
            {
                var obj = ddm.Objects[objectIndex];
                AddDdmObjectMeshNodes(
                    document,
                    BuildDdmObjectMeshes(document, obj, objectIndex, ddxTextures, textureDirs, sky),
                    BuildNodeName(nodePrefix, obj.Name, null, sky?.ObjectIndices.Contains(objectIndex) == true),
                    sky?.ObjectIndices.Contains(objectIndex) == true
                        ? sky.AnchorTransform
                        : Matrix4x4.Identity);
            }

            return;
        }

        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshSlotToDdm = DdmHashLookup.ResolveMeshIndices(psx, ddmByHash);
        var placedIndices = new HashSet<int>();
        var emittedSkyIndices = new HashSet<int>();
        var meshCache = new Dictionary<int, List<int>>();

        foreach (var psxObj in psx.Objects)
        {
            if (!meshSlotToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIndex) ||
                (uint)ddmIndex >= (uint)ddm.Objects.Count)
            {
                continue;
            }

            placedIndices.Add(ddmIndex);
            var isSky = sky?.ObjectIndices.Contains(ddmIndex) == true;
            if (isSky && !emittedSkyIndices.Add(ddmIndex))
                continue;

            if (!meshCache.TryGetValue(ddmIndex, out var meshIndices))
            {
                meshIndices = [];
                foreach (var mesh in BuildDdmObjectMeshes(
                             document, ddm.Objects[ddmIndex], ddmIndex, ddxTextures, textureDirs, sky))
                {
                    var addedIndex = ModelDocumentGeometryAdapter.AddMesh(document, mesh);
                    if (addedIndex.HasValue)
                        meshIndices.Add(addedIndex.Value);
                }

                meshCache[ddmIndex] = meshIndices;
            }

            var transform = isSky
                ? sky!.AnchorTransform
                : Matrix4x4.CreateTranslation(new Vector3(-psxObj.X, -psxObj.Y, psxObj.Z));
            foreach (var meshIndex in meshIndices)
            {
                var meshName = document.Meshes[meshIndex].Name;
                ModelDocumentGeometryAdapter.AddMeshNode(
                    document,
                    BuildNodeName(nodePrefix, meshName, psxObj.MeshIndex, isSky),
                    meshIndex,
                    transform);
            }
        }

        for (var objectIndex = 0; objectIndex < ddm.Objects.Count; objectIndex++)
        {
            if (placedIndices.Contains(objectIndex))
                continue;

            var obj = ddm.Objects[objectIndex];
            var isSky = sky?.ObjectIndices.Contains(objectIndex) == true;
            AddDdmObjectMeshNodes(
                document,
                BuildDdmObjectMeshes(document, obj, objectIndex, ddxTextures, textureDirs, sky),
                BuildNodeName(nodePrefix, obj.Name, null, isSky),
                isSky ? sky!.AnchorTransform : Matrix4x4.Identity);
        }
    }

    private static string BuildNodeName(
        string nodePrefix,
        string meshName,
        ushort? meshIndex,
        bool isSky)
    {
        var normalizedMeshName = meshName.StartsWith("sky__", StringComparison.Ordinal)
            ? meshName[5..]
            : meshName;
        var name = meshIndex.HasValue
            ? $"{nodePrefix}_{normalizedMeshName}_{meshIndex.Value:D4}"
            : $"{nodePrefix}_{normalizedMeshName}";
        return isSky ? "sky__" + name : name;
    }

    private static void AddDdmObjectMeshNodes(
        ModelDocument document,
        List<ModelMesh> meshes,
        string baseNodeName,
        Matrix4x4 transform)
    {
        foreach (var mesh in meshes)
        {
            var passIndex = mesh.Name.IndexOf("__pass", StringComparison.Ordinal);
            var nodeName = passIndex >= 0 ? baseNodeName + mesh.Name[passIndex..] : baseNodeName;
            ModelDocumentGeometryAdapter.AddMeshNode(document, nodeName, mesh, transform);
        }
    }

    /// <summary>
    ///     Build the object's meshes grouped by decal draw rank. The engine draws
    ///     splits in DdmMaterial.DrawOrder rank order (coplanar decal layers over
    ///     their base surface); vertices export at their AUTHORED positions — the
    ///     pre-conversion writer baked rank*0.1 (or a flat/blended 0.1) along the
    ///     vertex normals instead, corrupting every placed level's decal geometry.
    ///     Each rank &gt; 0 group becomes its own mesh carrying draw-order
    ///     metadata: glTF viewers order it via mesh extras / renderOrder, the
    ///     Blender importer separates it with a removable object-level offset
    ///     equal to the old bake.
    /// </summary>
    private static List<ModelMesh> BuildDdmObjectMeshes(
        ModelDocument document,
        DdmObject obj,
        int objectIndex,
        Dictionary<string, byte[]>? ddxTextures,
        List<string> textureDirs,
        DdmSkyClassifier.Result? sky = null)
    {
        var isSky = sky?.ObjectIndices.Contains(objectIndex) == true;
        var skyLayer = isSky ? sky!.LayerOrder.GetValueOrDefault(objectIndex) : 0;
        var meshName = isSky ? "sky__" + obj.Name : obj.Name;
        var baseMesh = new ModelMesh { Name = meshName };
        if (obj.Vertices.Count == 0 || obj.Indices.Length == 0)
            return [baseMesh];

        var materialIndices = AddDdmObjectMaterials(document, obj, ddxTextures, textureDirs);
        var minExtent = Math.Min(obj.BBoxExtentX, Math.Min(obj.BBoxExtentY, obj.BBoxExtentZ));
        var isFlat = minExtent < 1.5f;
        var drawOrderRanks = BuildDdmDrawOrderRanks(obj);
        var meshesByRank = new SortedDictionary<int, ModelMesh> { [0] = baseMesh };

        for (var splitIndex = 0; splitIndex < obj.Splits.Count; splitIndex++)
        {
            var split = obj.Splits[splitIndex];
            if (split.IndexCount < 3 || split.MaterialIndex >= obj.Materials.Count)
                continue;

            var mat = obj.Materials[split.MaterialIndex];
            var rank = drawOrderRanks.GetValueOrDefault(mat.DrawOrder);
            // The old bake was max(rank*0.1, flat-or-blended 0.1) — i.e. an
            // effective rank of max(rank, 1) for flat/blended splits with no
            // authored draw order (cross-object decal-on-wall coplanarity).
            var effectiveRank = Math.Max(rank, isFlat || mat.BlendMode != 0 ? 1 : 0);

            if (!meshesByRank.TryGetValue(effectiveRank, out var mesh))
            {
                mesh = new ModelMesh { Name = $"{meshName}__pass{effectiveRank}" };
                meshesByRank[effectiveRank] = mesh;
            }

            var primitive = AddDdmStripPrimitive(
                mesh,
                $"split_{splitIndex:D3}",
                materialIndices[split.MaterialIndex],
                obj,
                split);
            if (primitive != null && effectiveRank > 0)
            {
                var offset = ComputeDdmSplitAverageNormal(obj, split) *
                             (effectiveRank * DdmDecalNormalOffset);
                primitive.NativeMetadata.Add(new MeshDrawOrderMetadata(
                    effectiveRank,
                    effectiveRank,
                    objectIndex,
                    offset.X,
                    offset.Y,
                    offset.Z));
            }

            if (primitive != null && isSky)
                primitive.NativeMetadata.Add(new PsxSkyRenderMetadata(sky!.SkyColor, skyLayer));
        }

        return [.. meshesByRank.Values];
    }

    private static Vector3 ComputeDdmSplitAverageNormal(DdmObject obj, DdmSplit split)
    {
        var sum = Vector3.Zero;
        var end = Math.Min(obj.Indices.Length, split.IndexOffset + split.IndexCount);
        for (var i = split.IndexOffset; i < end; i++)
        {
            var vertexIndex = obj.Indices[i];
            if (vertexIndex >= obj.Vertices.Count)
                continue;

            var vertex = obj.Vertices[vertexIndex];
            sum += ModelDocumentGeometryAdapter.NormalizeOrDefault(
                new Vector3(-vertex.NX, -vertex.NY, vertex.NZ));
        }

        return ModelDocumentGeometryAdapter.NormalizeOrDefault(sum);
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

    private static ModelPrimitive? AddDdmStripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        DdmObject obj,
        DdmSplit split)
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

            var va = MakeDdmVertex(obj.Vertices[ai]);
            var vb = MakeDdmVertex(obj.Vertices[bi]);
            var vc = MakeDdmVertex(obj.Vertices[ci]);
            if ((i - split.IndexOffset) % 2 == 0)
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, va, vb, vc);
            else
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, vb, va, vc);
        }

        return ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices);
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

    private static ModelVertex MakeDdmVertex(DdmVertex vertex)
    {
        var normal = ModelDocumentGeometryAdapter.NormalizeOrDefault(new Vector3(-vertex.NX, -vertex.NY, vertex.NZ));
        var position = new Vector3(-vertex.X, -vertex.Y, vertex.Z);

        return new ModelVertex(
            position,
            normal,
            new Vector4(vertex.R / 255f, vertex.G / 255f, vertex.B / 255f, vertex.A / 255f),
            new Vector2(vertex.U, vertex.V));
    }
}
