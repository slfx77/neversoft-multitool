using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using SharpGLTF.Materials;
using SharpGLTF.Memory;

namespace NeversoftMultitool.Core.Formats.Psx;

using AlphaMode = SharpGLTF.Materials.AlphaMode;

internal sealed class PsxGltfMaterialContext
{
    public PsxGltfMaterialContext(PsxGltfWriter.TextureProvider? textureProvider)
    {
        TextureProvider = textureProvider;
        Untextured = CreateUntexturedMaterial();
    }

    public Dictionary<(uint Hash, bool SemiTrans), MaterialBuilder> Cache { get; } = new();

    public Dictionary<uint, (int Width, int Height)> TextureDimensions { get; } = new();

    public PsxGltfWriter.TextureProvider? TextureProvider { get; }

    public MaterialBuilder Untextured { get; }

    private static MaterialBuilder CreateUntexturedMaterial()
    {
        return new MaterialBuilder("untextured")
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1f));
    }
}

internal static class PsxGltfMaterialFactory
{
    internal static PsxGltfMaterialContext CreateContext(PsxGltfWriter.TextureProvider? textureProvider)
    {
        return new PsxGltfMaterialContext(textureProvider);
    }

    internal static (MaterialBuilder Material, (int Width, int Height) TexDims) ResolveFaceMaterial(
        PsxFace face,
        PsxGltfMaterialContext materials)
    {
        var material = face.IsTextured && face.TextureHash != 0
            ? GetOrCreateTexturedMaterial(face.TextureHash, face.IsSemiTransparent, materials)
            : materials.Untextured;

        var texDims = face.IsTextured
                      && face.TextureHash != 0
                      && materials.TextureDimensions.TryGetValue(face.TextureHash, out var dims)
            ? dims
            : (Width: 256, Height: 256);

        return (material, texDims);
    }

    private static MaterialBuilder GetOrCreateTexturedMaterial(
        uint textureHash,
        bool isSemiTransparent,
        PsxGltfMaterialContext materials)
    {
        var cacheKey = (textureHash, isSemiTransparent);
        if (materials.Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var name = QbKey.TryResolve(textureHash) ?? $"tex_{textureHash:X8}";
        if (isSemiTransparent)
            name += "__semitrans";

        var builder = new MaterialBuilder(name)
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(1, 1, 1, 1));

        if (materials.TextureProvider != null)
        {
            var pngBytes = materials.TextureProvider(textureHash);
            if (pngBytes != null)
            {
                var (processedPngBytes, hasAlpha) = GltfTextureHelper.ApplyColorKey(pngBytes);
                if (isSemiTransparent)
                {
                    processedPngBytes = GltfTextureHelper.ConvertLuminanceToAlpha(processedPngBytes);
                    hasAlpha = true;
                }

                var memImage = new MemoryImage(processedPngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
                if (hasAlpha)
                    builder.WithAlpha(isSemiTransparent ? AlphaMode.BLEND : AlphaMode.MASK, 0.5f);

                var dims = ExtractPngDimensions(processedPngBytes);
                if (dims.HasValue)
                    materials.TextureDimensions[textureHash] = dims.Value;
            }
        }

        materials.Cache[cacheKey] = builder;
        return builder;
    }

    private static (int Width, int Height)? ExtractPngDimensions(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < 24)
            return null;

        var width = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }
}
