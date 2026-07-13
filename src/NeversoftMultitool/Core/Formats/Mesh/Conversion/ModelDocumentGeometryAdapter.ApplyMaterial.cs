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
        int blendRate,
        MeshChecksumTextureResolver? textureProvider,
        Dictionary<uint, (int Width, int Height)> textureDims,
        Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int> materialCache)
    {
        var key = (textureHash, semiTransparent, doubleSided, blendRate);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var name = ResolveQbName(textureHash, $"tex_{textureHash:X8}");
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
