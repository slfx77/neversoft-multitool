using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
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
                AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
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
                var addedIndex = AddMesh(document, mesh);
                if (!addedIndex.HasValue)
                    continue;

                meshIndex = addedIndex.Value;
                meshCache[ddmIndex] = meshIndex;
            }

            AddMeshNode(
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
            AddMeshNode(document, $"{nodePrefix}_{obj.Name}", mesh);
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
            materialIndices[i] = AddMaterial(document, renderMaterial);
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
                AddTriangle(vertices, indices, va, vb, vc);
            else
                AddTriangle(vertices, indices, vb, va, vc);
        }

        AddPrimitive(mesh, name, materialIndex, vertices, indices);
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

    private static void PopulatePsxMeshNode(
        ModelDocument document,
        PsxMeshFile psxFile,
        int meshIndex,
        string nodeName,
        Matrix4x4 transform,
        Dictionary<(uint Hash, bool SemiTransparent), int> materialCache,
        Dictionary<uint, (int Width, int Height)> textureDims,
        int untexturedMaterial,
        MeshChecksumTextureResolver? textureProvider)
    {
        var psxMesh = psxFile.Meshes[meshIndex];
        if (psxMesh.Faces.Count == 0)
            return;

        var mesh = new ModelMesh { Name = ResolvePsxMeshName(psxFile, meshIndex) };
        foreach (var group in psxMesh.Faces.GroupBy(face =>
                     face.IsTextured && face.TextureHash != 0
                         ? (Hash: face.TextureHash, SemiTransparent: face.IsSemiTransparent)
                         : (Hash: 0u, SemiTransparent: false)))
        {
            var materialIndex = group.Key.Hash == 0
                ? untexturedMaterial
                : GetOrCreatePsxMaterial(
                    document,
                    group.Key.Hash,
                    group.Key.SemiTransparent,
                    textureProvider,
                    textureDims,
                    materialCache);

            var texDims = group.Key.Hash != 0 && textureDims.TryGetValue(group.Key.Hash, out var dims)
                ? dims
                : (Width: 256, Height: 256);
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var face in group)
                AddPsxFace(vertices, indices, psxFile.Version, psxMesh, face, psxFile.GouraudPalette, texDims);

            AddPrimitive(mesh, $"mat_{materialIndex:D3}", materialIndex, vertices, indices);
        }

        AddMeshNode(document, nodeName, mesh, transform);
    }

    private static void AddPsxFace(
        List<ModelVertex> vertices,
        List<int> indices,
        ushort version,
        PsxMesh mesh,
        PsxFace face,
        Vector4[]? gouraudPalette,
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = ComputePsxFaceColors(version, face, gouraudPalette);
        var v0 = MakePsxVertex(version, mesh, face, 0, c0, texDims);
        var v1 = MakePsxVertex(version, mesh, face, 1, c1, texDims);
        var v2 = MakePsxVertex(version, mesh, face, 2, c2, texDims);
        AddTriangle(vertices, indices, v0, v1, v2);

        if (face.IsQuad)
        {
            var v3 = MakePsxVertex(version, mesh, face, 3, c3, texDims);
            AddTriangle(vertices, indices, v1, v3, v2);
        }
    }

    private static ModelPrimitive? AddPs2StripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        Ps2Vertex[] sourceVertices,
        bool startsOnOddOutputSlot,
        HashSet<(Vector3, Vector3, Vector3)>? dedup,
        bool resetOnRestart,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite,
        int? skeletonIndex = null)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var influences = skeletonIndex.HasValue && Array.Exists(sourceVertices, static v => v.HasSkinData)
            ? new List<ModelBoneInfluences>()
            : null;
        var parityBias = startsOnOddOutputSlot ? 1 : 0;

        // Walk the strip and collect emitted triangles. When the source stream
        // has no normals, use the triangle's own face normal as a fallback; do
        // not smooth across strip positions because THAW worldzone leaves often
        // combine coplanar decals and sharp-edged pieces in one strip.
        var triangles = new List<(int A, int B, int C, Vector3 Normal)>();
        var stripStart = 0;

        for (var i = 0; i < sourceVertices.Length; i++)
        {
            var c = sourceVertices[i];
            var localIndex = i - stripStart;

            if (c.IsStripRestart)
            {
                // PS2 GS ADC semantics: vertex stays in the running strip but
                // the triangle ending at it is suppressed. Strip continues —
                // see the end-of-method commentary on the missing-triangle
                // bug if you're tempted to bring back resetOnRestart.
                continue;
            }

            if (localIndex < 2)
                continue;

            int aIdx, bIdx;
            if (((localIndex + parityBias) & 1) == 0)
            {
                aIdx = i - 2;
                bIdx = i - 1;
            }
            else
            {
                aIdx = i - 1;
                bIdx = i - 2;
            }

            var cIdx = i;

            var aPos = sourceVertices[aIdx].Position;
            var bPos = sourceVertices[bIdx].Position;
            var cPos = sourceVertices[cIdx].Position;

            if (IsDegenerate(aPos, bPos, cPos))
                continue;

            if (dedup is not null)
            {
                var key = SortedTriangleKey(aPos, bPos, cPos);
                if (!dedup.Add(key))
                    continue;
            }

            var faceNormal = Vector3.Cross(bPos - aPos, cPos - aPos);
            faceNormal = faceNormal.LengthSquared() > 1e-12f
                ? Vector3.Normalize(faceNormal)
                : Vector3.Zero;
            triangles.Add((aIdx, bIdx, cIdx, faceNormal));
        }

        foreach (var (aIdx, bIdx, cIdx, faceNormal) in triangles)
        {
            var fallbackNormal = faceNormal.LengthSquared() > 0f ? faceNormal : (Vector3?)null;
            var va = MakePs2Vertex(sourceVertices[aIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal);
            var vb = MakePs2Vertex(sourceVertices[bIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal);
            var vc = MakePs2Vertex(sourceVertices[cIdx], preserveVertexAlpha, bakeVertexColorsToWhite, fallbackNormal);
            if (influences is null)
            {
                AddTriangle(vertices, indices, va, vb, vc);
            }
            else
            {
                AddSkinnedTriangle(
                    vertices, indices, influences,
                    va, MakePs2SkinInfluence(sourceVertices[aIdx]),
                    vb, MakePs2SkinInfluence(sourceVertices[bIdx]),
                    vc, MakePs2SkinInfluence(sourceVertices[cIdx]));
            }
        }

        var skin = influences is { Count: > 0 }
            ? new ModelSkinBinding { SkeletonIndex = skeletonIndex!.Value, Influences = influences.ToArray() }
            : null;
        return AddPrimitive(mesh, name, materialIndex, vertices, indices, skin);
    }

    private static ModelBoneInfluences MakePs2SkinInfluence(Ps2Vertex vertex)
    {
        if (!vertex.HasSkinData)
            return ModelBoneInfluences.Single(0);
        return new ModelBoneInfluences(
            vertex.BoneIndex0, vertex.BoneIndex1, vertex.BoneIndex2, 0,
            vertex.BoneWeight0, vertex.BoneWeight1, vertex.BoneWeight2, 0f);
    }

    private static void AddXbxIndexedTriangles(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
        {
            var i0 = mesh.FaceIndices[i];
            var i1 = mesh.FaceIndices[i + 1];
            var i2 = mesh.FaceIndices[i + 2];
            if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
                continue;
            AddTriangle(
                vertices,
                indices,
                MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
        }
    }

    private static void AddXbxTriangleStrip(
        List<ModelVertex> vertices,
        List<int> indices,
        XbxMesh mesh,
        float coordinateScale)
    {
        for (var i = 2; i < mesh.FaceIndices.Length; i++)
        {
            var i0 = mesh.FaceIndices[i - 2];
            var i1 = mesh.FaceIndices[i - 1];
            var i2 = mesh.FaceIndices[i];
            if (i0 == i1 || i1 == i2 || i0 == i2 ||
                i0 >= mesh.Vertices.Length ||
                i1 >= mesh.Vertices.Length ||
                i2 >= mesh.Vertices.Length)
            {
                continue;
            }

            if (i % 2 == 0)
            {
                AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
            else
            {
                AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
        }
    }
}
