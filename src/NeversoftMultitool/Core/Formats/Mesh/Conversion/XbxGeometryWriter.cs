using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Xbox/PC THUG2+THAW scenes: indexed triangles and degenerate-strip
///     decoding with multi-pass materials.
/// </summary>
internal static class XbxGeometryWriter
{
    private const string OpaqueAlphaMode = "OPAQUE";

    public static void PopulateXbxScene(
        ModelDocument document,
        ParsedXbxScene scene,
        MeshChecksumTextureResolver? textureProvider,
        float coordinateScale = 1f,
        Ps2Skeleton? explicitSkeleton = null)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive number.");

        int? skeletonIndex = null;
        if (CanEmitExplicitSkin(scene, explicitSkeleton, coordinateScale))
        {
            skeletonIndex = document.Skeletons.Count;
            document.Skeletons.Add(Ps2SceneGeometryWriter.BuildPs2Skeleton(explicitSkeleton!));
        }

        var materialMap = new Dictionary<uint, int>();
        for (var i = 0; i < scene.Materials.Length && i < document.Materials.Count; i++)
        {
            materialMap[scene.Materials[i].Checksum] = i;
            ApplyXbxMaterial(document, document.Materials[i], scene.Materials[i], textureProvider);
        }

        foreach (var sector in scene.Sectors)
        {
            foreach (var xbxMesh in sector.Meshes)
            {
                if (xbxMesh.Vertices.Length < 3 || xbxMesh.FaceIndices.Length < 3)
                    continue;

                if (!materialMap.TryGetValue(xbxMesh.MaterialChecksum, out var materialIndex))
                    materialIndex = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
                    {
                        Name = ModelDocumentGeometryAdapter.ResolveQbName(xbxMesh.MaterialChecksum,
                            $"mat_{xbxMesh.MaterialChecksum:X8}")
                    });

                var mesh = new ModelMesh { Name = $"sector_{sector.Checksum:X8}" };
                var vertices = new List<ModelVertex>();
                var indices = new List<int>();
                var influences = skeletonIndex.HasValue && sector.IsSkinned
                    ? new List<ModelBoneInfluences>()
                    : null;
                AddXbxTriangles(vertices, indices, influences, xbxMesh, coordinateScale);

                var skin = influences is { Count: > 0 }
                    ? new ModelSkinBinding
                    {
                        SkeletonIndex = skeletonIndex!.Value,
                        Influences = influences.ToArray()
                    }
                    : null;
                ModelDocumentGeometryAdapter.AddPrimitive(
                    mesh, "triangles", materialIndex, vertices, indices, skin);
                ModelDocumentGeometryAdapter.AddMeshNode(document, mesh.Name, mesh);
            }
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void ApplyXbxMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        XbxMaterial material,
        MeshChecksumTextureResolver? textureProvider)
    {
        var (textureAlphaMode, bakeApplied) =
            RegisterPass0Texture(document, renderMaterial, material, textureProvider);

        // Pass-0 blend mode — not the texture's alpha histogram — decides
        // framebuffer blending: the engine alpha-BLENDS hair/overlay cards
        // (vBLEND_MODE_ADD..BLEND_FIXED = 1..6, DrawOrder-sorted, with
        // AlphaCutoff usually 1 just to kill a==0 texels). Classifying those
        // bimodal textures as MASK@cutoff-1/255 rendered every fringe texel
        // fully opaque WITH depth-write → hair-card z-fighting (billyjoe,
        // boone). Sorted alone never forces BLEND. A baked ADD/SUBTRACT texture
        // (modes 1-4) always framebuffer-blends, even when the SOURCE image was
        // opaque — the bake moved the blend strength into its alpha channel.
        var firstBlendMode = material.Passes.Length > 0 ? material.Passes[0].BlendMode : 0;
        var framebufferBlends = firstBlendMode is >= 1 and <= 6;
        if (framebufferBlends && (textureAlphaMode != OpaqueAlphaMode || bakeApplied))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
        else if (textureAlphaMode == "MASK" ||
                 (material.AlphaCutoff >= 1 && textureAlphaMode != OpaqueAlphaMode))
        {
            // Opaque framebuffer write + D3D alpha test (ALPHAREF 0-255, GEQUAL).
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = material.AlphaCutoff >= 1
                ? material.AlphaCutoff / 255f
                : 0.5f;
        }
    }

    /// <summary>
    ///     Resolves, bakes, and registers the material's document texture.
    ///     Returns the RAW pass-0 image's alpha classification (so the caller's
    ///     Blend/MASK decisions stay byte-identical to the pre-bake behaviour)
    ///     and whether a pass-0 ADD/SUBTRACT framebuffer bake was applied.
    /// </summary>
    private static (string TextureAlphaMode, bool BakeApplied) RegisterPass0Texture(
        ModelDocument document,
        RenderMaterial renderMaterial,
        XbxMaterial material,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider == null || material.Passes.Length == 0)
            return (OpaqueAlphaMode, false);

        var pass = material.Passes[0];
        if (pass.TextureChecksum == 0)
            return (OpaqueAlphaMode, false);

        var pngBytes = textureProvider(pass.TextureChecksum);
        if (pngBytes == null)
            return (OpaqueAlphaMode, false);

        var textureAlphaMode = Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);

        // Bake what glTF cannot express: composite pass-k overlays (in-shader
        // ADD/SUB/LERP layers — tattoos, detail maps) onto the pass-0 image,
        // then convert a pass-0 ADD/SUBTRACT framebuffer blend into the
        // portable alpha approximation.
        var finalPng = pngBytes;
        var composited = 0;
        if (material.Passes.Length > 1)
            (finalPng, composited) = XbxPassCompositor.CompositeOverlays(material, finalPng, textureProvider);

        var bakeApplied = XbxPassCompositor.IsFramebufferBakeMode(pass.BlendMode);
        if (bakeApplied)
        {
            finalPng = XbxPassCompositor.ApplyFramebufferBlendBake(finalPng, pass.BlendMode, pass.FixedAlpha);
            MarkBakedRecipe(renderMaterial, pass.BlendMode is 1 or 2 ? "additive" : "subtractive");
        }

        // Baked variants register under a synthetic checksum + name suffix so a
        // plain material sharing the same source texture keeps the pristine
        // copy (the PSX per-ABR-rate precedent).
        var checksum = composited > 0 || bakeApplied
            ? XbxPassCompositor.CreateSyntheticTextureChecksum(material)
            : pass.TextureChecksum;
        var name = ModelDocumentGeometryAdapter.ResolveQbName(pass.TextureChecksum,
                       $"tex_{pass.TextureChecksum:X8}") +
                   XbxPassCompositor.TextureNameSuffix(pass.BlendMode, composited);
        renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(
            document,
            name,
            finalPng,
            checksum,
            pass.UAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
            pass.VAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
            distinguishChecksumVariantsByContent: true);

        return (textureAlphaMode, bakeApplied);
    }

    /// <summary>
    ///     Records the baked shader recipe on the material's
    ///     <see cref="XbxMaterialRenderMetadata" /> so the Blender importer's
    ///     xbx_material branch can pick the matching Eevee recipe.
    /// </summary>
    private static void MarkBakedRecipe(RenderMaterial renderMaterial, string recipe)
    {
        for (var i = 0; i < renderMaterial.NativeMetadata.Count; i++)
        {
            if (renderMaterial.NativeMetadata[i] is XbxMaterialRenderMetadata xbxMeta)
                renderMaterial.NativeMetadata[i] = xbxMeta with { BakedRecipe = recipe };
        }
    }

    private static void AddXbxTriangles(
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences>? influences,
        XbxMesh mesh,
        float coordinateScale)
    {
        foreach (var (i0, i1, i2) in EnumerateTriangleIndices(mesh))
        {
            var v0 = MakeXbxVertex(mesh.Vertices[i0], coordinateScale);
            var v1 = MakeXbxVertex(mesh.Vertices[i1], coordinateScale);
            var v2 = MakeXbxVertex(mesh.Vertices[i2], coordinateScale);
            if (influences == null)
            {
                ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, v0, v1, v2);
            }
            else
            {
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    v0, MakeXbxSkinInfluence(mesh.Vertices[i0]),
                    v1, MakeXbxSkinInfluence(mesh.Vertices[i1]),
                    v2, MakeXbxSkinInfluence(mesh.Vertices[i2]));
            }
        }
    }

    private static IEnumerable<(int I0, int I1, int I2)> EnumerateTriangleIndices(XbxMesh mesh)
    {
        if (mesh.IsPreTriangulated)
        {
            for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
            {
                var i0 = mesh.FaceIndices[i];
                var i1 = mesh.FaceIndices[i + 1];
                var i2 = mesh.FaceIndices[i + 2];
                if (i0 < mesh.Vertices.Length && i1 < mesh.Vertices.Length && i2 < mesh.Vertices.Length)
                    yield return (i0, i1, i2);
            }

            yield break;
        }

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

            yield return i % 2 == 0 ? (i0, i1, i2) : (i1, i0, i2);
        }
    }

    internal static bool CanEmitExplicitSkin(
        ParsedXbxScene scene,
        Ps2Skeleton? skeleton,
        float coordinateScale)
    {
        if (skeleton == null || skeleton.Bones.Length == 0 ||
            BitConverter.SingleToInt32Bits(coordinateScale) != BitConverter.SingleToInt32Bits(1f))
            return false;

        var hasSkinnedTriangle = false;
        foreach (var sector in scene.Sectors)
        {
            if (!sector.IsSkinned)
                continue;

            foreach (var mesh in sector.Meshes)
            {
                foreach (var (i0, i1, i2) in EnumerateTriangleIndices(mesh))
                {
                    var v0 = mesh.Vertices[i0];
                    var v1 = mesh.Vertices[i1];
                    var v2 = mesh.Vertices[i2];
                    // Match AddSkinnedTriangle exactly: discarded geometry has
                    // no emitted corners, so its packed influence records are
                    // outside the output preflight.
                    if (ModelDocumentGeometryAdapter.IsDegenerate(v0.Position, v1.Position, v2.Position))
                        continue;

                    if (!HasValidInfluences(v0, skeleton.Bones.Length) ||
                        !HasValidInfluences(v1, skeleton.Bones.Length) ||
                        !HasValidInfluences(v2, skeleton.Bones.Length))
                    {
                        return false;
                    }

                    hasSkinnedTriangle = true;
                }
            }
        }

        return hasSkinnedTriangle;
    }

    private static bool HasValidInfluences(XbxVertex vertex, int jointCount)
    {
        // The sector flag says this record is skinned. An all-zero packed Xbox
        // record intentionally leaves HasSkinData false and binds rigidly to root.
        if (!vertex.HasSkinData)
            return true;

        var joints = new[]
        {
            vertex.BoneIndex0, vertex.BoneIndex1, vertex.BoneIndex2, vertex.BoneIndex3
        };
        var weights = new[]
        {
            vertex.BoneWeight0, vertex.BoneWeight1, vertex.BoneWeight2, vertex.BoneWeight3
        };
        var totalWeight = 0d;
        for (var i = 0; i < weights.Length; i++)
        {
            var weight = weights[i];
            if (!float.IsFinite(weight) || weight < 0f)
                return false;
            totalWeight += weight;
            if (weight > 0f && (joints[i] < 0 || joints[i] >= jointCount))
                return false;
        }

        return double.IsFinite(totalWeight) && totalWeight > 0d;
    }

    private static ModelBoneInfluences MakeXbxSkinInfluence(XbxVertex vertex)
    {
        if (!vertex.HasSkinData)
            return ModelBoneInfluences.Single(0);

        var weights = new[]
        {
            vertex.BoneWeight0, vertex.BoneWeight1, vertex.BoneWeight2, vertex.BoneWeight3
        };
        var joints = new[]
        {
            vertex.BoneIndex0, vertex.BoneIndex1, vertex.BoneIndex2, vertex.BoneIndex3
        };
        var totalWeight = weights.Sum(static weight => (double)weight);
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] > 0f)
                weights[i] = (float)(weights[i] / totalWeight);
            else
                joints[i] = 0;
        }

        return new ModelBoneInfluences(
            joints[0], joints[1], joints[2], joints[3],
            weights[0], weights[1], weights[2], weights[3]);
    }

    private static ModelVertex MakeXbxVertex(XbxVertex vertex, float coordinateScale)
    {
        // Vertex colours are decoded at /128 (raw 128 = neutral 1.0, up to ~2.0
        // overbright — the D3D MODULATE2X-family convention). Pass them through
        // UNCLAMPED: clamping here crushed every overbright highlight and
        // darkened THAW/THUG2 scenes wherever authored shading sat below 128.
        // The glTF exporter's out-of-range gate routes such meshes to the
        // overbright vertex layout (portable COLOR_0 clamped + raw values in
        // the _PSX_COLOR_0 float attribute), and the .blend path preserves the
        // floats in Blender's FLOAT_COLOR layer, so every consumer stays
        // spec-valid while the true modulation survives.
        var color = vertex.HasColor ? vertex.Color : Vector4.One;

        return new ModelVertex(
            vertex.Position * coordinateScale,
            vertex.HasNormal ? ModelDocumentGeometryAdapter.NormalizeOrDefault(vertex.Normal) : Vector3.UnitY,
            color,
            vertex.TexCoord);
    }
}
