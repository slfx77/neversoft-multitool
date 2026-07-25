using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PS2 MDL/SKIN scenes and GEOM trees: ADC-bit strip decoding, skeleton
///     binding, and vertex-alpha policy.
/// </summary>
internal static class Ps2SceneGeometryWriter
{
    public static void PopulatePs2Scene(
        ModelDocument document,
        ParsedPs2Scene scene,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Skeleton? skeleton = null)
    {
        var materialMap = new Dictionary<uint, int>();
        var nativeMaterials = new Dictionary<uint, Ps2Material>();
        foreach (var material in scene.Materials)
            nativeMaterials.TryAdd(material.Checksum, material);
        for (var i = 0; i < scene.Materials.Count && i < document.Materials.Count; i++)
        {
            materialMap[scene.Materials[i].Checksum] = i;
            Ps2MaterialWriter.ApplyPs2Material(document, document.Materials[i], scene.Materials[i], textureProvider);
        }

        int? skeletonIndex = null;
        if (skeleton != null)
        {
            skeletonIndex = document.Skeletons.Count;
            document.Skeletons.Add(BuildPs2Skeleton(skeleton));
        }

        // THPS4 (mesh version 4) strips carry no reliable winding: the ADC
        // stream mixes CW/CCW sub-strips (~half the triangles disagree with the
        // authored float32 normals — anl_cockatoo 192/400, Anl_Pigeon 13/29),
        // which culling/lit viewers show as missing faces ("incomplete") and
        // inverted shading. The stored normals are coherently outward
        // (normal-oriented signed volume is positive on the closed bodies), so
        // orient each emitted triangle to agree with them. THUG/THUG2
        // (mesh version 6) keep the raw strip order so their output is
        // byte-identical to previous releases.
        var orientToStoredNormals = scene.MeshVersion <= 4;

        var dedupByMaterial = new Dictionary<uint, HashSet<(Vector3, Vector3, Vector3)>>();
        // When the scene is skinned, fold every primitive into a single combined mesh
        // so the glTF exporter emits one skin shared across the whole character —
        // matching the legacy Ps2SceneGltfWriter behavior. Rigid scenes keep one
        // ModelMesh per native mesh group so per-group placement stays distinct.
        var skinnedMesh = skeletonIndex.HasValue ? new ModelMesh { Name = "skinned_mesh" } : null;
        foreach (var group in scene.MeshGroups)
        {
            var groupName = ModelDocumentGeometryAdapter.ResolveQbName(group.Checksum, $"group_{group.Checksum:X8}");
            foreach (var nativeMesh in group.Meshes)
            {
                if (nativeMesh.Vertices.Length < 3)
                    continue;

                if (!materialMap.TryGetValue(nativeMesh.MaterialChecksum, out var materialIndex))
                    materialIndex = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
                    {
                        Name = ModelDocumentGeometryAdapter.ResolveQbName(nativeMesh.MaterialChecksum,
                            $"mat_{nativeMesh.MaterialChecksum:X8}")
                    });

                if (!dedupByMaterial.TryGetValue(nativeMesh.MaterialChecksum, out var dedup))
                {
                    dedup = [];
                    dedupByMaterial[nativeMesh.MaterialChecksum] = dedup;
                }

                var mesh = skinnedMesh ?? new ModelMesh { Name = groupName };
                var preserveVertexAlpha =
                    !nativeMaterials.TryGetValue(nativeMesh.MaterialChecksum, out var nativeMaterial) ||
                    ShouldPreservePs2SceneVertexAlpha(nativeMaterial);
                AddPs2StripPrimitive(
                    mesh,
                    "strip",
                    materialIndex,
                    nativeMesh.Vertices,
                    nativeMesh.StartsOnOddOutputSlot,
                    dedup,
                    preserveVertexAlpha,
                    false,
                    skeletonIndex,
                    orientToStoredNormals);
                if (skinnedMesh == null)
                    ModelDocumentGeometryAdapter.AddMeshNode(document, groupName, mesh);
            }
        }

        if (skinnedMesh != null)
            ModelDocumentGeometryAdapter.AddMeshNode(document, skinnedMesh.Name, skinnedMesh);

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    public static void PopulatePs2Geom(
        ModelDocument document,
        Ps2GeomScene scene,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        for (var leafIndex = 0; leafIndex < scene.Leaves.Count; leafIndex++)
        {
            var leaf = scene.Leaves[leafIndex];
            if (leaf.Vertices.Length < 3)
                continue;

            var materialIndex = leafIndex < document.Materials.Count
                ? leafIndex
                : ModelDocumentGeometryAdapter.AddMaterial(document,
                    new RenderMaterial { Name = $"leaf_{leafIndex:D5}" });
            Ps2MaterialWriter.ApplyPs2GeomMaterial(document, document.Materials[materialIndex], leaf, textureProvider,
                tex0Resolver);

            var mesh = new ModelMesh { Name = $"leaf_{leafIndex:D5}" };
            var alphaMode = Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
            var preserveVertexAlpha = ShouldPreservePs2GeomVertexAlpha(leaf, alphaMode);
            AddPs2StripPrimitive(
                mesh,
                "strip",
                materialIndex,
                leaf.Vertices,
                false,
                null,
                preserveVertexAlpha,
                false);
            ModelDocumentGeometryAdapter.AddMeshNode(document, mesh.Name, mesh);
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    internal static ModelPrimitive? AddPs2StripPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        Ps2Vertex[] sourceVertices,
        bool startsOnOddOutputSlot,
        HashSet<(Vector3, Vector3, Vector3)>? dedup,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite,
        int? skeletonIndex = null,
        bool orientToStoredNormals = false)
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

            if (ModelDocumentGeometryAdapter.IsDegenerate(aPos, bPos, cPos))
                continue;

            if (dedup is not null)
            {
                var key = ModelDocumentGeometryAdapter.SortedTriangleKey(aPos, bPos, cPos);
                if (!dedup.Add(key))
                    continue;
            }

            var faceNormal = Vector3.Cross(bPos - aPos, cPos - aPos);
            faceNormal = faceNormal.LengthSquared() > 1e-12f
                ? Vector3.Normalize(faceNormal)
                : Vector3.Zero;

            // THPS4 strips have no reliable winding — orient each triangle so
            // its geometric normal agrees with the authored vertex normals
            // (see the mesh-version gate in PopulatePs2Scene).
            if (orientToStoredNormals &&
                sourceVertices[aIdx].HasNormal &&
                sourceVertices[bIdx].HasNormal &&
                sourceVertices[cIdx].HasNormal)
            {
                var storedNormalSum = sourceVertices[aIdx].Normal
                                      + sourceVertices[bIdx].Normal
                                      + sourceVertices[cIdx].Normal;
                if (Vector3.Dot(faceNormal, storedNormalSum) < 0f)
                {
                    (aIdx, bIdx) = (bIdx, aIdx);
                    faceNormal = -faceNormal;
                }
            }

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
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, va, vb, vc);
            }
            else
            {
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    va, MakePs2SkinInfluence(sourceVertices[aIdx]),
                    vb, MakePs2SkinInfluence(sourceVertices[bIdx]),
                    vc, MakePs2SkinInfluence(sourceVertices[cIdx]));
            }
        }

        var skin = influences is { Count: > 0 }
            ? new ModelSkinBinding { SkeletonIndex = skeletonIndex!.Value, Influences = influences.ToArray() }
            : null;
        return ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices, skin);
    }

    private static ModelBoneInfluences MakePs2SkinInfluence(Ps2Vertex vertex)
    {
        if (!vertex.HasSkinData)
            return ModelBoneInfluences.Single(0);
        return new ModelBoneInfluences(
            vertex.BoneIndex0, vertex.BoneIndex1, vertex.BoneIndex2, 0,
            vertex.BoneWeight0, vertex.BoneWeight1, vertex.BoneWeight2, 0f);
    }

    internal static ModelSkeleton BuildPs2Skeleton(Ps2Skeleton skeleton)
    {
        // PS2 .ske bones store local rotation+translation in glTF-friendly coordinates
        // (no Z-up→Y-up swap needed). The IR's LocalTransform mirrors what SharpGLTF
        // would flatten an AffineTransform to: row-vector M = R * T.
        var result = new ModelSkeleton { Name = "skeleton" };
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var local = Matrix4x4.CreateFromQuaternion(bone.LocalRotation)
                        * Matrix4x4.CreateTranslation(bone.LocalTranslation);
            result.Bones.Add(new ModelBone
            {
                Name = QbKey.QbKey.TryResolve(bone.NameChecksum) ?? $"bone_{bone.NameChecksum:X8}",
                ParentIndex = bone.ParentIndex,
                LocalTransform = local,
                InverseBindMatrix = bone.InverseBindMatrix,
                NativeChecksum = bone.NameChecksum
            });
        }

        return result;
    }

    private static bool ShouldPreservePs2SceneVertexAlpha(Ps2Material material)
    {
        var fixedOpacity = material.FixedBlendOpacity;
        return !fixedOpacity.HasValue ||
               fixedOpacity.Value >= Ps2SceneRenderSemantics.FixBlendOpaqueThreshold / 128f;
    }

    internal static bool ShouldPreservePs2GeomVertexAlpha(Ps2GeomLeaf leaf, string alphaMode)
    {
        if (!string.Equals(alphaMode, "BLEND", StringComparison.Ordinal))
            return true;

        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        return Ps2GeomRenderSemantics.BlendUsesSourceAlpha(alphaBlend);
    }

    private static ModelVertex MakePs2Vertex(
        Ps2Vertex vertex,
        bool preserveVertexAlpha,
        bool bakeVertexColorsToWhite,
        Vector3? fallbackNormal = null)
    {
        var r = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.R / 128f, 1f);
        var g = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.G / 128f, 1f);
        var b = bakeVertexColorsToWhite ? 1f : Math.Min(vertex.B / 128f, 1f);
        var a = !bakeVertexColorsToWhite && preserveVertexAlpha ? Math.Min(vertex.A / 128f, 1f) : 1f;

        // Worldzone leaves rarely carry per-vertex normals in their source VIF
        // batches — `vertex.HasNormal == false` is common — so callers may pass
        // a flat face normal as a fallback for optional lighting and viewer
        // normals. Source vertex colours do not depend on this fallback.
        var hasUsableNormal = vertex.HasNormal || fallbackNormal.HasValue;
        var normal = vertex.HasNormal
            ? ModelDocumentGeometryAdapter.NormalizeOrDefault(vertex.Normal)
            : fallbackNormal ?? Vector3.UnitY;

        if (!bakeVertexColorsToWhite && ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting is { } lighting &&
            hasUsableNormal)
        {
            var diffuse = MathF.Max(0f, Vector3.Dot(normal, lighting.SunDirection));
            var rf = lighting.Ambient.X + diffuse * lighting.SunColor.X;
            var gf = lighting.Ambient.Y + diffuse * lighting.SunColor.Y;
            var bf = lighting.Ambient.Z + diffuse * lighting.SunColor.Z;
            r = Math.Min(r * rf, 1f);
            g = Math.Min(g * gf, 1f);
            b = Math.Min(b * bf, 1f);
        }

        return new ModelVertex(
            vertex.Position,
            normal,
            new Vector4(r, g, b, a),
            vertex.HasUV ? new Vector2(vertex.U, 1f - vertex.V) : Vector2.Zero);
    }
}
