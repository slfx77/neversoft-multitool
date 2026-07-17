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
///     PS2 material construction: GS blend/alpha-test classification shared by
///     the scene, geom, and worldzone writers.
/// </summary>
internal static class Ps2MaterialWriter
{
    private static ModelTextureWrap Ps2ClampToWrap(uint mode)
    {
        return mode is 1 or 2 ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat;
    }

    internal static void ApplyPs2Material(
        ModelDocument document,
        RenderMaterial renderMaterial,
        Ps2Material material,
        MeshChecksumTextureResolver? textureProvider)
    {
        byte[]? pngBytes = null;
        if (textureProvider != null && material.TextureChecksum != 0)
        {
            pngBytes = textureProvider(material.TextureChecksum);
            if (pngBytes != null)
            {
                renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(
                    document,
                    ModelDocumentGeometryAdapter.ResolveQbName(material.TextureChecksum, $"tex_{material.TextureChecksum:X8}"),
                    pngBytes,
                    material.TextureChecksum,
                    material.ClampU ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat,
                    material.ClampV ? ModelTextureWrap.ClampToEdge : ModelTextureWrap.Repeat);
            }
        }

        // AREF <= 1 is the GS default always-pass alpha test (ATE=1/AGEQUAL/AREF=1
        // kills only a == 0) — a pass-through the engine programs unconditionally,
        // NOT a cutout. Exporting it as MASK@1/128 forced every semi-transparent
        // overlay (stubble/hair cards) fully opaque — the THAW "corrupted textures"
        // regression introduced by 884d018. Only a deliberately programmed higher
        // AREF exports as a mask cutoff.
        if ((material.Flags & (uint)Ps2MaterialFlags.Transparent) == 0)
        {
            if (material.AlphaRef >= 2)
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = GsAlphaRefToCutoff(material.AlphaRef);
            }

            return;
        }

        if (material.IsOpaqueBlend)
        {
            // A non-default alpha reference explicitly requests a cutout. Otherwise,
            // infer cutout versus graduated transparency from the texture itself so
            // partially transparent details such as hair remain visible.
            if (material.AlphaRef >= 2)
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = GsAlphaRefToCutoff(material.AlphaRef);
                return;
            }

            if (pngBytes != null &&
                Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes) == "BLEND")
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Blend;
                return;
            }

            renderMaterial.AlphaMode = ModelAlphaMode.Mask;
            return;
        }

        var fixedOpacity = material.FixedBlendOpacity;
        if (fixedOpacity.HasValue && fixedOpacity.Value >= Ps2SceneRenderSemantics.FixBlendOpaqueThreshold / 128f)
            return;

        // Dest-alpha blend (C = Ad): glTF has no destination alpha. Opaque base
        // passes write A=128 so Ad resolves to 1.0 and the layer reduces to Cs —
        // export OPAQUE (or the real alpha test when one is programmed) instead
        // of BLEND, which disabled depth-write and z-fought (Crown envmap layer).
        var blendCSource = (int)((material.RegAlpha >> 4) & 0x3);
        if (blendCSource == 1)
        {
            if (material.AlphaRef >= 2)
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = GsAlphaRefToCutoff(material.AlphaRef);
            }

            return;
        }

        // Standard source-alpha blend whose texture is really a hard-edged
        // cutout (or that carries a real alpha test): de-escalate to MASK so
        // depth-write stays on — mirrors the geom path's classification.
        // An AREF of one or less (the always-pass default) is not a real test,
        // so graduated overlays remain blended when that default is used.
        if (pngBytes != null &&
            Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend((byte)(material.RegAlpha & 0xFF)))
        {
            var textureAlphaMode = Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes);
            if (material.AlphaRef >= 2 || textureAlphaMode == "MASK")
            {
                renderMaterial.AlphaMode = ModelAlphaMode.Mask;
                renderMaterial.AlphaCutoff = material.AlphaRef >= 2
                    ? GsAlphaRefToCutoff(material.AlphaRef)
                    : 0.5f;
                return;
            }
        }

        renderMaterial.AlphaMode = ModelAlphaMode.Blend;
        if (fixedOpacity.HasValue)
            renderMaterial.BaseColor = new Vector4(1f, 1f, 1f, fixedOpacity.Value);
    }

    internal static void ApplyPs2GeomMaterial(
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
                renderMaterial.TextureIndex ??= ModelDocumentGeometryAdapter.AddTexture(
                    document,
                    ModelDocumentGeometryAdapter.ResolveQbName(textureChecksum, $"tex_{textureChecksum:X8}"),
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
            // Guard: MASK can be reached via texture classification while the
            // GS test register only holds the engine's always-pass default —
            // a computed cutoff of 0 would make the material fully opaque.
            renderMaterial.AlphaCutoff =
                useTextureAlphaMode || !Ps2GeomRenderSemantics.UsesAlphaTestMask(leaf.DmaTest1)
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

    internal static string ClassifyPs2GeomEffectiveAlphaMode(
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
        if (alphaMode == "BLEND" && Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend))
        {
            if (Ps2GeomSourceAlphaIsOpaque(leaf, pngBytes))
            {
                return Ps2GeomRenderSemantics.UsesAlphaTestMask(leaf.DmaTest1)
                    ? "MASK"
                    : "OPAQUE";
            }

            // Bimodal cutout textures (crosses, fringes) render better as MASK:
            // hard edges are preserved and depth-write stays on.
            if (pngBytes != null &&
                Ps2GeomDestinationAlphaSynthesis.ClassifyTextureAlphaMode(pngBytes) == "MASK")
            {
                return "MASK";
            }
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
}
