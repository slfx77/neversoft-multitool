using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
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
                renderMaterial.TextureIndex ??= AddTexture(document, material.TextureName, pngBytes);
                renderMaterial.AlphaMode = isAdditive || loaded.Value.HasAlpha
                    ? ModelAlphaMode.Blend
                    : material.BlendMode == 2
                        ? ModelAlphaMode.Mask
                        : ModelAlphaMode.Opaque;
            }
        }

        if (isAdditive)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (material.BlendMode == 2)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }

    private static void ApplyPs2Material(
        ModelDocument document,
        RenderMaterial renderMaterial,
        Ps2Material material,
        MeshChecksumTextureResolver? textureProvider)
    {
        if (textureProvider != null && material.TextureChecksum != 0)
        {
            var pngBytes = textureProvider(material.TextureChecksum);
            if (pngBytes != null)
            {
                renderMaterial.TextureIndex ??= AddTexture(
                    document,
                    ResolveQbName(material.TextureChecksum, $"tex_{material.TextureChecksum:X8}"),
                    pngBytes,
                    material.TextureChecksum,
                    material.ClampU ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                    material.ClampV ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
            }
        }

        if ((material.Flags & (uint)Ps2MaterialFlags.Transparent) == 0)
        {
            if (material.AlphaRef >= 1)
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = GsAlphaRefToCutoff(material.AlphaRef);
            }

            return;
        }

        if (material.IsOpaqueBlend)
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            // Alpha-tested cutout (hair, clothing fringes). Use the AREF the game's own
            // DIRECT block programmed when we captured one; the 0.5 default only covers
            // entries whose setup block carried no alpha test.
            if (material.AlphaRef >= 1)
                renderMaterial.AlphaCutoff = GsAlphaRefToCutoff(material.AlphaRef);
            return;
        }

        var fixedOpacity = material.FixedBlendOpacity;
        if (fixedOpacity.HasValue && fixedOpacity.Value >= Ps2SceneRenderSemantics.FixBlendOpaqueThreshold / 128f)
            return;

        renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        if (fixedOpacity.HasValue)
            renderMaterial.BaseColor = new Vector4(1f, 1f, 1f, fixedOpacity.Value);
    }

    private static void ApplyPs2GeomMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        Ps2GeomLeaf leaf,
        MeshChecksumTextureResolver? textureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        Ps2TexaTextureResolver? texaTextureProvider = null,
        uint? textureChecksumOverride = null,
        bool useTextureAlphaMode = false,
        string? alphaModeOverride = null)
    {
        var textureChecksum = textureChecksumOverride
                              ?? (leaf.TextureChecksum != 0
                                  ? leaf.TextureChecksum
                                  : tex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum) ?? 0);
        byte[]? pngBytes = null;
        if ((textureProvider != null || texaTextureProvider != null) && textureChecksum != 0)
        {
            pngBytes = texaTextureProvider?.Invoke(textureChecksum, leaf.DmaTexa)
                       ?? textureProvider?.Invoke(textureChecksum);
            if (pngBytes != null)
            {
                renderMaterial.TextureIndex ??= AddTexture(
                    document,
                    ResolveQbName(textureChecksum, $"tex_{textureChecksum:X8}"),
                    pngBytes,
                    textureChecksum,
                    Ps2ClampToWrap((uint)(leaf.DmaClamp1 & 0x3)),
                    Ps2ClampToWrap((uint)((leaf.DmaClamp1 >> 2) & 0x3)));
            }
        }

        var alphaMode = alphaModeOverride ?? ClassifyPs2GeomEffectiveAlphaMode(leaf, pngBytes, useTextureAlphaMode);
        if (alphaMode == "MASK")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = useTextureAlphaMode
                ? 0.5f
                : Ps2GeomRenderSemantics.ComputeAlphaMaskCutoff(leaf.DmaTest1);
            return;
        }

        if (alphaMode == "BLEND")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
            ApplyPs2FixedBlendAlpha(renderMaterial, leaf.DmaAlpha1);
        }
    }

    private static string ClassifyPs2GeomEffectiveAlphaMode(
        Ps2GeomLeaf leaf,
        byte[]? pngBytes,
        bool useTextureAlphaMode)
    {
        if (useTextureAlphaMode && pngBytes != null)
        {
            return Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
        }

        if (Ps2GeomDestinationAlphaSynthesis.ShouldFallbackToSourceAlphaBlend(leaf))
            return "BLEND";

        var alphaMode = Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (alphaMode == "BLEND" &&
            Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend) &&
            Ps2GeomSourceAlphaIsOpaque(leaf, pngBytes))
        {
            return Ps2GeomRenderSemantics.UsesAlphaTestMask(leaf.DmaTest1)
                ? "MASK"
                : "OPAQUE";
        }

        return alphaMode;
    }

    private static bool Ps2GeomSourceAlphaIsOpaque(Ps2GeomLeaf leaf, byte[]? pngBytes)
    {
        if (pngBytes == null ||
            Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes) != "OPAQUE")
        {
            return false;
        }

        return leaf.Vertices.All(static vertex => vertex.IsStripRestart || vertex.A >= 128);
    }

    /// <summary>
    ///     Convert a GS alpha-test reference (raw GS byte, 128 = nominal 1.0) to a glTF
    ///     MASK cutoff. Exported PNG alpha is rescaled by 255/128, so the cutoff must be
    ///     rescaled the same way: AREF/128 clamped to 1.0.
    /// </summary>
    private static float GsAlphaRefToCutoff(int alphaRef)
    {
        return Math.Min(alphaRef / 128f, 1f);
    }

    private static void ApplyPs2FixedBlendAlpha(RenderMaterial renderMaterial, ulong alpha)
    {
        var alphaBlend = (byte)(alpha & 0xFF);
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;
        if (aField != 0 || bField != 1 || cField != 2 || dField != 1)
            return;

        var opacity = Math.Clamp(((alpha >> 32) & 0xFF) / 128f, 0f, 1f);
        renderMaterial.BaseColor = new Vector4(
            renderMaterial.BaseColor.X,
            renderMaterial.BaseColor.Y,
            renderMaterial.BaseColor.Z,
            opacity);
    }

    private static void ApplyXbxMaterial(
        ModelDocument document,
        RenderMaterial renderMaterial,
        XbxMaterial material,
        MeshChecksumTextureResolver? textureProvider)
    {
        var textureAlphaMode = "OPAQUE";
        if (textureProvider != null && material.Passes.Length > 0)
        {
            var pass = material.Passes[0];
            if (pass.TextureChecksum != 0)
            {
                var pngBytes = textureProvider(pass.TextureChecksum);
                if (pngBytes != null)
                {
                    textureAlphaMode = Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
                    renderMaterial.TextureIndex ??= AddTexture(
                        document,
                        ResolveQbName(pass.TextureChecksum, $"tex_{pass.TextureChecksum:X8}"),
                        pngBytes,
                        pass.TextureChecksum,
                        pass.UAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                        pass.VAddressing == 3 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
                }
            }
        }

        var firstBlendMode = material.Passes.Length > 0 ? material.Passes[0].BlendMode : 0;
        if (textureAlphaMode == "BLEND" && (firstBlendMode != 0 || material.Sorted))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
        else if (textureAlphaMode == "MASK" ||
                 (material.AlphaCutoff >= 1 && textureAlphaMode != "OPAQUE"))
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            renderMaterial.AlphaCutoff = material.AlphaCutoff >= 1
                ? material.AlphaCutoff / 255f
                : 0.5f;
        }
        else if (textureAlphaMode == "BLEND")
        {
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        }
    }

    private static int AddRwMaterial(
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
        return AddMaterial(document, renderMaterial);
    }

    private static void ApplyRwMaterial(
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

                renderMaterial.TextureIndex ??= AddTexture(document, material.TextureName, pngBytes);
            }
        }

        if (material.A < 255 || material.IsBlend)
            renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        else if (textureHasAlpha)
            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
    }

    private static int GetOrCreatePsxMaterial(
        ModelDocument document,
        uint textureHash,
        bool semiTransparent,
        bool doubleSided,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided), int> materialCache)
    {
        var key = (textureHash, semiTransparent, doubleSided);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var name = ResolveQbName(textureHash, $"tex_{textureHash:X8}");
        if (semiTransparent)
            name += "__semitrans";
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
                    processed = MeshTextureHelper.ConvertLuminanceToAlpha(processed);
                    hasAlpha = true;
                }

                material.TextureIndex = AddTexture(document, name, processed, textureHash);
                if (hasAlpha)
                    material.AlphaMode = semiTransparent ? ModelAlphaMode.Blend : ModelAlphaMode.Mask;
                if (TryExtractPngDimensions(processed) is { } dims)
                    textureDims[textureHash] = dims;
            }
        }

        var index = AddMaterial(document, material);
        materialCache[key] = index;
        return index;
    }

    private static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputePsxFaceColors(
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

    private static Vector3 ComputePsxVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        var normalIndex = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? vertexIndex
            : face.NormalIndex;
        if (normalIndex >= mesh.Normals.Count)
            return Vector3.UnitY;

        var normal = mesh.Normals[(int)normalIndex];
        return NormalizeOrDefault(new Vector3(normal.X, -normal.Y, -normal.Z));
    }

    private static Vector2 ComputePsxTextureUv(
        ushort version,
        PsxFace face,
        int u,
        int v,
        int texWidth,
        int texHeight)
    {
        if (!face.IsTextured)
            return Vector2.Zero;

        return version == 0x06
            ? new Vector2(u / 512f, v / 512f)
            : new Vector2(u / (float)Math.Max(texWidth, 1), v / (float)Math.Max(texHeight, 1));
    }

    private static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
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

    private static bool UsesCombinedPsxCharacterAssembly(PsxMeshFile psxFile)
    {
        return psxFile.HasHierarchy ||
               psxFile.Meshes.Any(static mesh => mesh.Vertices.Any(static vertex =>
                   PsxMeshSemantics.IsExactStitchedReference(vertex.Type)));
    }

    private static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    private static string ResolvePsxMeshName(PsxMeshFile psxFile, int meshIndex)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length ? psxFile.MeshNameHashes[meshIndex] : 0u;
        return ResolveQbName(nameHash, $"mesh_{meshIndex:X8}");
    }
}
