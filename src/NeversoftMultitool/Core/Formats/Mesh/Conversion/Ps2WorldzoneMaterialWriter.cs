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
///     Worldzone material cache: TEXA-aware texture resolution, GS metadata,
///     and the redundant destination-alpha blend-layer filter.
/// </summary>
internal static class Ps2WorldzoneMaterialWriter
{
    internal readonly record struct Ps2WorldzoneMaterialKey(
        uint TextureChecksum,
        ulong Clamp,
        ulong Alpha,
        ulong Test,
        ulong Texa,
        uint GroupChecksum,
        bool IsBillboard,
        string AlphaMode);

    internal static bool ShouldSkipRedundantWorldzoneBlendLayer(
        Ps2GeomLeaf leaf,
        uint textureChecksum,
        Ps2DestinationAlphaLeafGeometryKey geometryKey,
        Dictionary<Ps2DestinationAlphaLeafGeometryKey, Ps2DestinationAlphaMaskCandidate> recentAlphaMasks)
    {
        if (textureChecksum == 0 || leaf.IsBillboard)
            return false;

        var alphaBlend = (byte)(leaf.DmaAlpha1 & 0xFF);
        if (!Ps2GeomRenderSemantics.IsStandardSourceAlphaBlend(alphaBlend))
            return false;

        if (!recentAlphaMasks.TryGetValue(geometryKey, out var previous) ||
            previous.TextureChecksum != textureChecksum)
        {
            return false;
        }

        var previousAlphaBlend = (byte)(previous.Leaf.DmaAlpha1 & 0xFF);
        if (previousAlphaBlend is not (0x0A or 0x1A or 0x00))
            return false;

        var (min, max) = Ps2WorldzoneGeometryWriter.ComputeBbox(leaf.Vertices);
        var size = max - min;
        var maxDimension = Math.Max(Math.Abs(size.X), Math.Max(Math.Abs(size.Y), Math.Abs(size.Z)));
        return maxDimension >= 250f;
    }

    internal static int GetOrCreatePs2WorldzoneMaterial(
        ModelDocument document,
        Dictionary<Ps2WorldzoneMaterialKey, int> materialCache,
        Ps2GeomLeaf leaf,
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        uint? textureChecksumOverride = null,
        bool useTextureAlphaMode = false,
        string? alphaModeOverride = null)
    {
        var textureChecksum = textureChecksumOverride ?? ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
        var alphaModeKey = alphaModeOverride ?? Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode(leaf);
        var key = new Ps2WorldzoneMaterialKey(
            textureChecksum,
            leaf.DmaClamp1 & 0x0F,
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            leaf.DmaTexa,
            leaf.GroupChecksum,
            leaf.IsBillboard,
            alphaModeKey);
        if (materialCache.TryGetValue(key, out var existing))
            return existing;

        var materialName = textureChecksum != 0
            ? ModelDocumentGeometryAdapter.ResolveQbName(textureChecksum, $"tex_{textureChecksum:X8}")
            : "default";
        var renderMaterial = new RenderMaterial
        {
            Name = $"{materialName}_{materialCache.Count:D5}"
        };
        renderMaterial.NativeMetadata.Add(MakePs2GsMetadata(
            leaf,
            tex0Resolver,
            "ps2_worldzone_material",
            textureChecksum));
        Ps2MaterialWriter.ApplyPs2GeomMaterial(
            document,
            renderMaterial,
            leaf,
            textureProvider,
            tex0Resolver,
            texaTextureProvider,
            textureChecksum,
            useTextureAlphaMode,
            alphaModeOverride);
        var index = ModelDocumentGeometryAdapter.AddMaterial(document, renderMaterial);
        materialCache[key] = index;
        return index;
    }

    internal static Ps2GsRenderMetadata MakePs2GsMetadata(
        Ps2GeomLeaf leaf,
        Ps2Tex0ChecksumResolver? tex0Resolver,
        string source,
        uint? textureChecksumOverride = null)
    {
        var textureChecksum = textureChecksumOverride ?? ResolvePs2GeomTextureChecksum(leaf, tex0Resolver);
        return new Ps2GsRenderMetadata(
            leaf.DmaAlpha1,
            leaf.DmaTest1,
            leaf.DmaTex0,
            leaf.DmaTex1,
            leaf.DmaTexa,
            leaf.DmaClamp1,
            textureChecksum != 0 ? textureChecksum : null,
            leaf.GroupChecksum,
            (int)((leaf.DmaTest1 >> 4) & 0xFF),
            source,
            leaf.DmaFrame1);
    }

    internal static uint ResolvePs2GeomTextureChecksum(
        Ps2GeomLeaf leaf,
        Ps2Tex0ChecksumResolver? tex0Resolver)
    {
        return leaf.TextureChecksum != 0
            ? leaf.TextureChecksum
            : tex0Resolver?.Invoke(leaf.DmaTex0, leaf.GroupChecksum) ?? 0;
    }

    internal static Ps2TexaTextureResolver? ResolvePs2TexaAwareProvider(
        MeshChecksumTextureResolver? textureProvider,
        Ps2TexaTextureResolver? texaTextureProvider)
    {
        if (texaTextureProvider != null)
            return texaTextureProvider;
        if (textureProvider == null)
            return null;
        return (checksum, _) => textureProvider(checksum);
    }
}
