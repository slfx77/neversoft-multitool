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
        NdsTextureSource? textures = null)
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
            var scale = TexCoordScale(group.Material);

            for (var i = 0; i + 2 < group.Indices.Count; i += 3)
            {
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices,
                    indices,
                    Convert(group.Vertices[group.Indices[i]], scale),
                    Convert(group.Vertices[group.Indices[i + 1]], scale),
                    Convert(group.Vertices[group.Indices[i + 2]], scale));
            }

            ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices);
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

        var texture = TryAddTexture(document, key, textures);
        if (texture.HasValue)
            material.TextureIndex = texture.Value;
        return material;
    }

    /// <summary>
    ///     Decodes and embeds the bank record the sub-object names. Paletted DS
    ///     formats key transparency on colour 0, so a decoded texture can carry real
    ///     holes; those export as a mask rather than blending, matching the hardware
    ///     which discards rather than blends them.
    /// </summary>
    private static int? TryAddTexture(
        ModelDocument document, NdsMaterialKey key, NdsTextureSource? textures)
    {
        if (textures == null || !key.HasTexture || key.TextureIndex < 0)
            return null;
        if (key.TextureIndex >= textures.Bank.Count)
            return null;

        var entry = textures.Bank[key.TextureIndex];
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

        return ModelDocumentGeometryAdapter.AddTexture(
            document,
            $"tex_{entry.PixelId:x8}",
            ImageWriter.WritePngToMemory(entry.Width, entry.Height, rgba),
            entry.PixelId,
            Wrap(key.RepeatS, key.MirrorS),
            Wrap(key.RepeatT, key.MirrorT));
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
        return new ModelVertex(
            new Vector3(p.X, p.Z, -p.Y),
            Vector3.UnitY,
            vertex.Color,
            vertex.TexCoord * scale);
    }
}
