using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
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
        float coordinateScale = 1f)
    {
        if (!float.IsFinite(coordinateScale) || coordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(coordinateScale), coordinateScale,
                "Coordinate scale must be a finite positive number.");

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
                if (xbxMesh.IsPreTriangulated)
                    AddXbxIndexedTriangles(vertices, indices, xbxMesh, coordinateScale);
                else
                    AddXbxTriangleStrip(vertices, indices, xbxMesh, coordinateScale);

                ModelDocumentGeometryAdapter.AddPrimitive(mesh, "triangles", materialIndex, vertices, indices);
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
            pass.VAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);

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
            ModelDocumentGeometryAdapter.AddTriangle(
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
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
            else
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    MakeXbxVertex(mesh.Vertices[i1], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i0], coordinateScale),
                    MakeXbxVertex(mesh.Vertices[i2], coordinateScale));
            }
        }
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
