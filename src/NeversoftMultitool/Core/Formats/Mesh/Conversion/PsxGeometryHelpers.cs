using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX face/vertex decode helpers shared by the character-skin,
///     level-strip, and vertex-factory paths of
///     <see cref="ModelDocumentGeometryAdapter" />.
/// </summary>
internal static class PsxGeometryHelpers
{
    internal static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputePsxFaceColors(
        ushort version,
        PsxFace face,
        Vector4[]? gouraudPalette)
    {
        if (face.IsGouraud && gouraudPalette != null && version != 0x06)
        {
            var c0 = face.R < gouraudPalette.Length ? gouraudPalette[face.R] : Vector4.One;
            var c1 = face.G < gouraudPalette.Length ? gouraudPalette[face.G] : Vector4.One;
            var c2 = face.B < gouraudPalette.Length ? gouraudPalette[face.B] : Vector4.One;
            var c3 = face.IsQuad && face.Mode < gouraudPalette.Length ? gouraudPalette[face.Mode] : c0;
            return (c0, c1, c2, c3);
        }

        var flat = face.IsGouraud
            ? Vector4.One
            : new Vector4(
                Math.Min(face.R / 128f, 1f),
                Math.Min(face.G / 128f, 1f),
                Math.Min(face.B / 128f, 1f),
                1f);
        return (flat, flat, flat, flat);
    }

    internal static Vector3 ComputePsxVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        var normalIndex = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? vertexIndex
            : face.NormalIndex;
        if (normalIndex >= mesh.Normals.Count)
            return Vector3.UnitY;

        var normal = mesh.Normals[(int)normalIndex];
        return ModelDocumentGeometryAdapter.NormalizeOrDefault(new Vector3(normal.X, -normal.Y, -normal.Z));
    }

    internal static Vector2 ComputePsxTextureUv(
        ushort version,
        PsxFace face,
        int u,
        int v,
        int texWidth,
        int texHeight)
    {
        if (!face.IsTextured)
            return Vector2.Zero;

        // v6 (Spider-Man DC/PC port containers) stores UVs as u16/i16 pairs
        // in a fixed 512-texel normalized space instead of the PS1-era byte
        // texel coordinates that address the texture directly. The /512 is an
        // EMPIRICAL constant — established against the BLACKCAT.PSX locked
        // fixture and the DC/PC level sweeps (validator-clean, textures land
        // correctly) — not a decomp-verified contract; the DC exe is SH-4,
        // outside the MIPS decomp toolkit.
        return version == 0x06
            ? new Vector2(u / 512f, v / 512f)
            : new Vector2(u / (float)Math.Max(texWidth, 1), v / (float)Math.Max(texHeight, 1));
    }

    internal static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    internal static bool UsesCombinedPsxCharacterAssembly(PsxMeshFile psxFile)
    {
        // A HIER chunk alone is not a character marker: level files carry one
        // for their placed animated objects (THPS1-proto skdown/skvans) and
        // must stay on the per-object level path — routing them through the
        // combined skinned assembly scatters hundreds of meshes across bind
        // pivots ("dust" renders). IsSuperModel bounds the part count.
        return psxFile.HasStitchedReferences ||
               (psxFile.HasHierarchy && psxFile.IsSuperModel);
    }

    internal static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    internal static string ResolvePsxMeshName(PsxMeshFile psxFile, int meshIndex)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length ? psxFile.MeshNameHashes[meshIndex] : 0u;
        return ModelDocumentGeometryAdapter.ResolveQbName(nameHash, $"mesh_{meshIndex:X8}");
    }

    internal static int GetOrCreatePsxMaterial(
        ModelDocument document,
        uint textureHash,
        bool semiTransparent,
        bool doubleSided,
        int blendRate,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache)
    {
        var key = (textureHash, semiTransparent, doubleSided, blendRate);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var name = ModelDocumentGeometryAdapter.ResolveQbName(textureHash, $"tex_{textureHash:X8}");
        if (semiTransparent)
            name += $"__st{blendRate}";
        if (doubleSided)
            name += "__2sided";

        // PS1 backface-culls every face unless flag bit 9 is set
        // (M3dAsm_ProcessPolys @0x80099B04), so PSX materials are
        // single-sided by default — unlike the RenderMaterial default.
        var material = new RenderMaterial
        {
            Name = name,
            AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque,
            DoubleSided = doubleSided
        };

        if (textureProvider != null)
        {
            var pngBytes = textureProvider(textureHash);
            if (pngBytes != null)
            {
                var (processed, hasAlpha) = MeshTextureHelper.ApplyColorKey(pngBytes);
                if (semiTransparent)
                {
                    processed = ConvertPsxSemiTransparentTexture(processed, blendRate);
                    hasAlpha = true;
                }

                material.TextureIndex = ModelDocumentGeometryAdapter.AddTexture(document, name, processed, textureHash);
                if (hasAlpha)
                    material.AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Mask;
                if (ModelDocumentGeometryAdapter.TryExtractPngDimensions(processed) is { } dims)
                    textureDims[textureHash] = dims;
            }
        }

        var index = ModelDocumentGeometryAdapter.AddMaterial(document, material);
        materialCache[key] = index;
        return index;
    }

    /// <summary>
    ///     Bakes one of the four PS1 ABR blend equations into the texture,
    ///     since glTF has only OPAQUE/MASK/BLEND (face_flag_semantics.md §3b;
    ///     conversions mirror the RW BSP precedent in RwBspGltfWriter):
    ///     rate 0 (0.5B+0.5F, the common glass/water/shadow average) keeps the
    ///     texture's hue at uniform 50% alpha; rate 1 (B+F additive) bakes
    ///     luminance-to-alpha (dark→transparent, bright→white glow); rate 2
    ///     (B−F subtractive) darkens via black RGB with brightness-driven
    ///     alpha; rate 3 (B+0.25F) is quarter-strength additive.
    /// </summary>
    private static byte[] ConvertPsxSemiTransparentTexture(byte[] pngBytes, int blendRate)
    {
        return blendRate switch
        {
            1 => MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes),
            2 => MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0),
            3 => MeshTextureHelper.ScaleTextureAlpha(
                MeshTextureHelper.ConvertLuminanceToAlpha(pngBytes), 0.25f),
            _ => MeshTextureHelper.ScaleTextureAlpha(pngBytes, 0.5f)
        };
    }
}
