using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX character assembly: object-parent skeleton, combined skinned mesh
///     binned by material, single-bone influences via the resolver.
/// </summary>
internal static class PsxSkinnedGeometryWriter
{
    /// <summary>
    ///     Character-hierarchy populator for PSX models. Emits a skeleton derived
    ///     from the object parent chain (one bone per object), a combined skinned
    ///     mesh that bins faces by material, and per-vertex single-bone influences
    ///     resolved via <see cref="PsxCharacterMeshResolver" /> (stitched + attachment
    ///     vertices follow their source body part).
    ///     PSX character rendering is piecewise-rigid: the engine builds one
    ///     world-space SMatrix per body part and renders each part against its
    ///     own matrix. The exported glTF keeps the authored parent hierarchy for
    ///     bind placement, while animation channels compensate for glTF's parent
    ///     rotation chaining.
    /// </summary>
    internal static void PopulatePsxSkinned(
        ModelDocument document,
        PsxMeshFile psxFile,
        PshFile? pshFile,
        MeshChecksumTextureResolver? textureProvider,
        bool flatSkeleton,
        IReadOnlySet<int>? flatBoneIndices,
        PsxMeshFile? splineClawFile,
        MeshChecksumTextureResolver? splineClawTextureProvider,
        IReadOnlySet<int>? hiddenObjectIndices,
        bool reconstructSplineAppendages)
    {
        var skeletonIndex = document.Skeletons.Count;
        document.Skeletons.Add(BuildPsxSkeleton(
            psxFile, pshFile, flatSkeleton, flatBoneIndices));

        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache = new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();
        var untexturedMaterial = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = "untextured",
            BaseColor = Vector4.One,
            DoubleSided = false
        });

        var lodVariants = PsxGeometryHelpers.BuildPsxLodVariantSet(psxFile);
        var splineChains = PsxSplineAppendageGeometry.FindControllerChains(psxFile);
        var splineControllerObjects = PsxSplineAppendageGeometry.BuildControllerObjectSet(splineChains);
        var embeddedTipPlacements = PsxSplineAppendageGeometry.FindEmbeddedTipPlacements(
            psxFile, splineChains);
        var buckets = new Dictionary<int, PsxSkinnedBucket>();
        // claw.psx is a separate model and its small numeric texture
        // checksums can overlap the parent character. Keep its material and
        // dimension caches isolated so a parent material cannot steal a claw
        // or tentacle image merely because the native hash is the same.
        var splineTextureDims = new Dictionary<uint, (int Width, int Height)>();
        var splineMaterialCache =
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            if (hiddenObjectIndices?.Contains(objectIndex) == true
                || splineControllerObjects.Contains(objectIndex)
                || (!reconstructSplineAppendages && embeddedTipPlacements.ContainsKey(objectIndex)))
                continue;

            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count || lodVariants.Contains(meshIndex))
                continue;

            var psxMesh = psxFile.Meshes[meshIndex];
            if (psxMesh.Faces.Count == 0)
                continue;

            foreach (var face in psxMesh.Faces)
            {
                var (materialIndex, texDims) = ResolvePsxFaceMaterial(
                    document, face, textureProvider, textureDims, materialCache, untexturedMaterial);

                if (!buckets.TryGetValue(materialIndex, out var bucket))
                {
                    bucket = new PsxSkinnedBucket();
                    buckets[materialIndex] = bucket;
                }

                AddPsxSkinnedFace(
                    bucket.Vertices, bucket.Indices, bucket.Influences,
                    psxFile, objectIndex, meshIndex, psxMesh, face, texDims,
                    embeddedTipPlacements.TryGetValue(objectIndex, out var tipPlacement)
                        ? tipPlacement
                        : null);
            }
        }

        if (reconstructSplineAppendages && splineChains.Count > 0)
        {
            var tubeTextureHash = splineClawFile == null
                ? PsxSplineAppendageGeometry.FindEmbeddedTailTextureHash(
                    psxFile, embeddedTipPlacements, textureProvider)
                : PsxSplineAppendageGeometry.FindTubeTextureHash(
                    splineClawFile, splineClawTextureProvider);
            var tubeTextureProvider = splineClawFile == null
                ? textureProvider
                : splineClawTextureProvider;
            var tubeTextureDims = splineClawFile == null
                ? textureDims
                : splineTextureDims;
            var tubeMaterialCache = splineClawFile == null
                ? materialCache
                : splineMaterialCache;
            var tubeMaterial = tubeTextureHash is { } hash
                ? PsxGeometryHelpers.GetOrCreatePsxMaterial(
                    document,
                    hash,
                    false,
                    false,
                    0,
                    tubeTextureProvider,
                    tubeTextureDims,
                    tubeMaterialCache)
                : untexturedMaterial;

            if (!buckets.TryGetValue(tubeMaterial, out var bucket))
            {
                bucket = new PsxSkinnedBucket();
                buckets[tubeMaterial] = bucket;
            }

            PsxSplineAppendageGeometry.AppendGeneratedTubes(
                splineChains,
                bucket.Vertices,
                bucket.Indices,
                bucket.Influences,
                tubeTextureHash.HasValue);
        }

        if (reconstructSplineAppendages
            && splineChains.Count > 1
            && splineClawFile is { Meshes.Count: > 0 })
        {
            const int clawObjectIndex = 0;
            const int clawMeshIndex = 0;
            var clawMesh = splineClawFile.Meshes[clawMeshIndex];
            var clawForwardSign = PsxSplineAppendageGeometry.DetermineTipForwardSign(clawMesh);

            foreach (var chain in splineChains)
            {
                var placement = PsxSplineAppendageGeometry.CreateTipPlacement(
                    chain, clawForwardSign);
                foreach (var face in clawMesh.Faces)
                {
                    var (materialIndex, texDims) = ResolvePsxFaceMaterial(
                        document, face, splineClawTextureProvider,
                        splineTextureDims, splineMaterialCache, untexturedMaterial);
                    if (!buckets.TryGetValue(materialIndex, out var bucket))
                    {
                        bucket = new PsxSkinnedBucket();
                        buckets[materialIndex] = bucket;
                    }

                    AddPsxSkinnedFace(
                        bucket.Vertices, bucket.Indices, bucket.Influences,
                        splineClawFile, clawObjectIndex, clawMeshIndex,
                        clawMesh, face, texDims, placement);
                }
            }
        }

        var combinedMesh = new ModelMesh { Name = "combined_mesh" };
        foreach (var (materialIndex, bucket) in buckets)
        {
            ModelDocumentGeometryAdapter.AddPrimitive(combinedMesh, $"mat_{materialIndex:D3}", materialIndex,
                bucket.Vertices, bucket.Indices,
                new ModelSkinBinding
                {
                    SkeletonIndex = skeletonIndex,
                    Influences = bucket.Influences.ToArray()
                });
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, "combined_mesh", combinedMesh);
    }

    private static ModelSkeleton BuildPsxSkeleton(
        PsxMeshFile psxFile,
        PshFile? pshFile,
        bool flatSkeleton,
        IReadOnlySet<int>? flatBoneIndices)
    {
        var skeleton = new ModelSkeleton { Name = "skeleton" };
        var objects = psxFile.Objects;
        var bonePositionsGltf = new Vector3[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            bonePositionsGltf[i] = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(psxFile, objects[i]));
        }

        for (var i = 0; i < objects.Count; i++)
        {
            var worldBindGltf = bonePositionsGltf[i];
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, i);
            var name = pshFile?.GetBoneName(i)
                       ?? (meshIndex >= 0 ? PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex) : null)
                       ?? $"bone_{i}";

            var parent = flatSkeleton || flatBoneIndices?.Contains(i) == true
                ? -1
                : objects[i].ParentIndex;
            if (parent < 0 || parent >= objects.Count || parent == i)
                parent = -1;

            var localBindGltf = parent >= 0
                ? worldBindGltf - bonePositionsGltf[parent]
                : worldBindGltf;

            skeleton.Bones.Add(new ModelBone
            {
                Name = name,
                ParentIndex = parent,
                LocalTransform = Matrix4x4.CreateTranslation(localBindGltf),
                InverseBindMatrix = Matrix4x4.CreateTranslation(-worldBindGltf)
            });
        }

        return skeleton;
    }

    private static (int MaterialIndex, (int Width, int Height) TexDims) ResolvePsxFaceMaterial(
        ModelDocument document,
        PsxFace face,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache,
        int untexturedMaterial)
    {
        var key = PsxGeometryHelpers.GetPsxMaterialKey(face);

        var materialIndex = key.Hash == 0 &&
                            !key.SemiTransparent &&
                            !key.DoubleSided
            ? untexturedMaterial
            : PsxGeometryHelpers.GetOrCreatePsxMaterial(document, key.Hash, key.SemiTransparent, key.DoubleSided,
                key.BlendRate, textureProvider, textureDims, materialCache);

        var texDims = key.Hash != 0 && textureDims.TryGetValue(key.Hash, out var dims)
            ? dims
            : (Width: 256, Height: 256);
        return (materialIndex, texDims);
    }

    private static void AddPsxSkinnedFace(
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        PsxMesh mesh,
        PsxFace face,
        (int Width, int Height) texDims,
        PsxSplineTipPlacement? tipPlacement)
    {
        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(
            psxFile.Version, mesh, face, psxFile.GouraudPalette);
        c0 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c0);
        c1 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c1);
        c2 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c2);
        c3 = PsxGeometryHelpers.ApplyPsxUntexturedBlend(face, c3);
        var isPs1 = psxFile.Version != 0x06;
        var isPs1TexturedModulation = isPs1 && face.IsTextured;
        var packetUsesTexturedScale = face.IsTextured &&
                                      (face.TextureHash != 0 || !face.IsSemiTransparent);
        Vector4? p0 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c0, packetUsesTexturedScale)
            : null;
        Vector4? p1 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c1, packetUsesTexturedScale)
            : null;
        Vector4? p2 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c2, packetUsesTexturedScale)
            : null;
        Vector4? p3 = isPs1
            ? PsxGeometryHelpers.ToPsxPacketColor(c3, packetUsesTexturedScale)
            : null;
        c0 = PsxGeometryHelpers.DisplayRgbToLinear(c0, isPs1TexturedModulation);
        c1 = PsxGeometryHelpers.DisplayRgbToLinear(c1, isPs1TexturedModulation);
        c2 = PsxGeometryHelpers.DisplayRgbToLinear(c2, isPs1TexturedModulation);
        c3 = PsxGeometryHelpers.DisplayRgbToLinear(c3, isPs1TexturedModulation);
        var v0 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 0, c0, p0, texDims, tipPlacement,
            out var i0);
        var v1 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 1, c1, p1, texDims, tipPlacement,
            out var i1);
        var v2 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 2, c2, p2, texDims, tipPlacement,
            out var i2);
        // glTF front faces are CCW; PSX slot order is CW under the (X,-Y,-Z)
        // handedness map, so emit reversed to make winding agree with the
        // stored (outward) normals. Probe: psx_lod_part_probe.py --normals.
        ModelDocumentGeometryAdapter.AddSkinnedTriangle(vertices, indices, influences, v0, i0, v2, i2, v1, i1);

        if (face.IsQuad)
        {
            var v3 = MakePsxSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 3, c3, p3, texDims, tipPlacement,
                out var i3);
            ModelDocumentGeometryAdapter.AddSkinnedTriangle(vertices, indices, influences, v1, i1, v2, i2, v3, i3);
        }
    }

    private static ModelVertex MakePsxSkinnedVertex(
        PsxMeshFile psxFile,
        int objectIndex,
        int meshIndex,
        PsxMesh mesh,
        PsxFace face,
        int slot,
        Vector4 color,
        Vector4? psxPacketColor,
        (int Width, int Height) texDims,
        PsxSplineTipPlacement? tipPlacement,
        out ModelBoneInfluences influence)
    {
        var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);

        var normalMesh = mesh;
        var normalVertexIndex = vertexIndex;
        if (resolved is
            {
                UsedAttachment: true, AttachmentResolved: true, SourceMeshIndex: >= 0, SourceVertexIndex: >= 0
            }
            && resolved.SourceMeshIndex < psxFile.Meshes.Count)
        {
            var candidate = psxFile.Meshes[resolved.SourceMeshIndex];
            if (candidate.HasPerVertexNormals && resolved.SourceVertexIndex < candidate.VertexCount)
            {
                normalMesh = candidate;
                normalVertexIndex = (uint)resolved.SourceVertexIndex;
            }
        }

        var worldPosition = resolved.WorldPosition;
        var normal = PsxGeometryHelpers.ComputePsxVertexNormal(normalMesh, face, normalVertexIndex);
        var jointIndex = resolved.SourceObjectIndex >= 0 ? resolved.SourceObjectIndex : objectIndex;
        if (tipPlacement is { } placement)
        {
            var source = mesh.Vertices[(int)vertexIndex];
            worldPosition = placement.TransformPosition(new Vector3(source.X, source.Y, source.Z));
            var nativeNormal = PsxMeshSemantics.ToGltfPosition(normal);
            normal = Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(
                placement.TransformDirection(nativeNormal)));
            jointIndex = placement.JointIndex;
        }

        var texCoord = face.GetTextureCoordinate(slot);
        var psxPrimitiveFlags = Vector3.Zero;
        if (psxPacketColor.HasValue)
        {
            var texturedFlag = face.IsTextured &&
                               (face.TextureHash != 0 || !face.IsSemiTransparent)
                ? 1f
                : 0f;
            var gouraudFlag = face.IsGouraud ? 1f : 0f;
            psxPrimitiveFlags = new Vector3(texturedFlag, gouraudFlag, 1f);
        }

        var vertex = new ModelVertex(
            PsxMeshSemantics.ToGltfPosition(worldPosition),
            normal,
            color,
            PsxGeometryHelpers.ComputePsxTextureUv(psxFile.Version, face, texCoord.U, texCoord.V, texDims.Width,
                texDims.Height))
        {
            PsxPacketColor = psxPacketColor,
            PsxPrimitiveFlags = psxPrimitiveFlags,
            TextureWibble = ModelTextureWibble.FromFace(psxFile.Version, face, slot, texDims)
        };

        influence = ModelBoneInfluences.Single(jointIndex);
        return vertex;
    }

    private sealed class PsxSkinnedBucket
    {
        public List<ModelVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];
        public List<ModelBoneInfluences> Influences { get; } = [];
    }
}
