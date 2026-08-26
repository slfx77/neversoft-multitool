using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Supplies a DS model's texture images: the bank its display list draws from,
///     and the texel bytes for a record in that bank. Both live in the GOB container
///     rather than beside the model, so the caller owns the lookup.
/// </summary>
public sealed record NdsTextureSource(
    IReadOnlyList<NdsTextureEntry> Bank,
    Func<uint, byte[]?> ReadTexels);

/// <summary>
///     Binds decoded vertices to skeleton bones by matrix provenance. The DS has no
///     per-vertex weights — a vertex belongs entirely to the matrix that transformed
///     it — so every influence is a single joint at weight 1.
/// </summary>
public sealed class NdsSkinAssignment
{
    public required int SkeletonIndex { get; init; }
    public required IReadOnlyDictionary<int, int> BoneByProvenance { get; init; }

    public ModelBoneInfluences InfluenceOf(in NdsVertex vertex)
    {
        return ModelBoneInfluences.Single(
            BoneByProvenance.TryGetValue(vertex.Matrix, out var bone) ? bone : 0);
    }
}

/// <summary>
///     Turns a decoded DS display list into a <see cref="ModelDocument" />: one mesh
///     per render state, with vertex colours, UVs and — where the model's texture
///     bank can be identified — the texture image itself.
///
///     Two conversions happen here rather than in the interpreter, which stays in
///     native console space. The DS models are Z-up and glTF is Y-up, so positions
///     rotate by (x, y, z) -> (x, z, -y); that is a rotation, not a mirror, so
///     triangle winding is preserved. And texcoords arrive in TEXELS, which become
///     UVs by dividing by the size the material's own TEXIMAGE_PARAM declares —
///     values outside 0..1 are ordinary tiling (a road surface repeats ~15 times),
///     which is why the wrap mode has to be carried across too.
/// </summary>
internal static class NdsGeometryWriter
{
    public static void PopulateNdsGeometry(
        ModelDocument document,
        NdsGeometryFile file,
        IReadOnlyList<NdsGeometryGroup> groups,
        NdsTextureSource? textures = null,
        NdsSkinAssignment? skin = null)
    {
        var index = 0;
        foreach (var group in groups)
        {
            if (group.Indices.Count == 0)
                continue;

            var name = $"mesh_{index:D3}";
            var material = BuildMaterial(document, group.Material, index, textures);
            var materialIndex = ModelDocumentGeometryAdapter.AddMaterial(document, material);

            var mesh = new ModelMesh { Name = name };
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            var influences = skin != null ? new List<ModelBoneInfluences>() : null;
            var scale = TexCoordScale(group.Material);

            for (var i = 0; i + 2 < group.Indices.Count; i += 3)
            {
                var a = group.Vertices[group.Indices[i]];
                var b = group.Vertices[group.Indices[i + 1]];
                var c = group.Vertices[group.Indices[i + 2]];
                if (influences != null)
                {
                    ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                        vertices, indices, influences,
                        Convert(a, scale), skin!.InfluenceOf(a),
                        Convert(b, scale), skin.InfluenceOf(b),
                        Convert(c, scale), skin.InfluenceOf(c));
                }
                else
                {
                    ModelDocumentGeometryAdapter.AddTriangle(
                        vertices, indices, Convert(a, scale), Convert(b, scale), Convert(c, scale));
                }
            }

            var binding = influences == null
                ? null
                : new ModelSkinBinding
                {
                    SkeletonIndex = skin!.SkeletonIndex,
                    Influences = [.. influences]
                };
            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, name, materialIndex, vertices, indices, binding);
            ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
            index++;
        }

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static RenderMaterial BuildMaterial(
        ModelDocument document, NdsMaterialKey key, int index, NdsTextureSource? textures)
    {
        var alpha = key.Alpha;
        var material = new RenderMaterial
        {
            Name = MaterialName(key, index),
            // Polygon alpha 0 means "wireframe" on hardware, not invisible; only
            // genuine 1..30 values are translucent.
            BaseColor = new Vector4(1f, 1f, 1f, alpha is > 0 and < 31 ? alpha / 31f : 1f),
            AlphaMode = alpha is > 0 and < 31 ? ModelAlphaMode.Blend : ModelAlphaMode.Opaque,
            DoubleSided = DisplaysBothFaces(key.PolygonAttr)
        };

        var texture = TryAddTexture(document, key, textures, out var entry);
        if (texture.HasValue)
        {
            material.TextureIndex = texture.Value;
            if (entry != null && material.AlphaMode == ModelAlphaMode.Opaque)
            {
                // The hardware's per-texel transparency lives in the BANK record,
                // not the model's blank TEXIMAGE_PARAM sites (the runtime patches
                // the whole word in, colour-0 bit included) — so the material has
                // to consult the bound entry. Colour-0 holes and Direct16's 1-bit
                // alpha are binary: MASK. A3I5/A5I3 carry graduated alpha: BLEND.
                // Without this the key colour parked in palette slot 0 (magenta,
                // green — its value is dead data on hardware) rendered opaque.
                if (entry.Format is NdsTextureFormat.A3I5 or NdsTextureFormat.A5I3)
                {
                    material.AlphaMode = ModelAlphaMode.Blend;
                }
                else if (entry.Color0Transparent || entry.Format == NdsTextureFormat.Direct16)
                {
                    material.AlphaMode = ModelAlphaMode.Mask;
                    material.AlphaCutoff = 0.5f;
                }
            }
        }

        return material;
    }

    /// <summary>
    ///     Decodes and embeds the bank record the sub-object names. Paletted DS
    ///     formats key transparency on colour 0, so a decoded texture can carry real
    ///     holes; those export as a mask rather than blending, matching the hardware
    ///     which discards rather than blends them.
    /// </summary>
    private static int? TryAddTexture(
        ModelDocument document, NdsMaterialKey key, NdsTextureSource? textures,
        out NdsTextureEntry? boundEntry)
    {
        boundEntry = null;
        if (textures == null || !key.HasTexture || key.TextureIndex < 0)
            return null;
        if (key.TextureIndex >= textures.Bank.Count)
            return null;

        var entry = textures.Bank[key.TextureIndex];
        boundEntry = entry;
        if (entry.Format == NdsTextureFormat.Compressed4X4)
            return null;

        var texels = textures.ReadTexels(entry.PixelId);
        if (texels == null || texels.Length < entry.PixelBytes)
            return null;

        byte[] rgba;
        try
        {
            rgba = NdsTextureDecoder.Decode(entry, texels);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        // The decoder y-flips the bottom-up art into upright PNGs; flip it BACK
        // for embedding so the mesh can keep its UVs in the hardware's own texel
        // space. Sampling then matches the console exactly — floor semantics,
        // integer-texel island borders, tiling and mirroring included — where a
        // UV-side V flip is one texel row off at every authored border.
        FlipRows(rgba, entry.Width, entry.Height);

        return ModelDocumentGeometryAdapter.AddTexture(
            document,
            $"tex_{entry.PixelId:x8}",
            ImageWriter.WritePngToMemory(entry.Width, entry.Height, rgba),
            entry.PixelId,
            Wrap(key.RepeatS, key.MirrorS),
            Wrap(key.RepeatT, key.MirrorT),
            nearestFilter: true);
    }

    private static void FlipRows(byte[] rgba, int width, int height)
    {
        var stride = width * 4;
        Span<byte> swap = stackalloc byte[4096];
        var row = swap[..Math.Min(stride, swap.Length)];
        for (var y = 0; y < height / 2; y++)
        {
            var top = rgba.AsSpan(y * stride, stride);
            var bottom = rgba.AsSpan((height - 1 - y) * stride, stride);
            for (var x = 0; x < stride; x += row.Length)
            {
                var chunk = Math.Min(row.Length, stride - x);
                top.Slice(x, chunk).CopyTo(row);
                bottom.Slice(x, chunk).CopyTo(top.Slice(x, chunk));
                row[..chunk].CopyTo(bottom.Slice(x, chunk));
            }
        }
    }

    /// <summary>GX flip only mirrors while repeat is on; without repeat the edge clamps.</summary>
    private static ModelTextureWrap Wrap(bool repeat, bool mirror)
    {
        if (!repeat)
            return ModelTextureWrap.ClampToEdge;
        return mirror ? ModelTextureWrap.MirroredRepeat : ModelTextureWrap.Repeat;
    }

    private static string MaterialName(NdsMaterialKey key, int index)
    {
        if (!key.HasTexture)
            return $"mat_{index:D3}_untextured";
        var slot = key.TextureIndex >= 0 ? $"tex{key.TextureIndex:D3}" : "texUnbound";
        return $"mat_{index:D3}_{slot}_{key.TextureFormat}_{key.TextureWidth}x{key.TextureHeight}";
    }

    /// <summary>POLYGON_ATTR bits 6-7: 0 none, 1 back, 2 front, 3 both.</summary>
    private static bool DisplaysBothFaces(uint polygonAttr)
    {
        return ((polygonAttr >> 6) & 3) == 3;
    }

    private static Vector2 TexCoordScale(NdsMaterialKey key)
    {
        return key.HasTexture
            ? new Vector2(1f / key.TextureWidth, 1f / key.TextureHeight)
            : Vector2.One;
    }

    private static ModelVertex Convert(NdsVertex vertex, Vector2 scale)
    {
        var p = vertex.Position;
        // UVs stay in the hardware's own coordinate space (raw texels / size).
        // The art is stored bottom-up, but the flip lives in the EMBEDDED image
        // (TryAddTexture re-flips the decoder's upright rows) rather than here:
        // a "v = 1 - t/h" UV flip is exact only at texel CENTRES, and DS
        // coordinates are 12.4 texels sampled by floor, with island borders
        // authored at exact integers — flipping V put every such border one
        // texel row off and drew seams down the atlas boundaries.
        return new ModelVertex(
            new Vector3(p.X, p.Z, -p.Y),
            Vector3.UnitY,
            vertex.Color,
            vertex.TexCoord * scale);
    }
}
