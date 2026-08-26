using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Binds the skater's rendered corners to the bone-per-vertex rig
///     <see cref="GbaAnimatedModelWriter" /> builds: one bone per unique model
///     vertex, so a corner's influence is simply its source vertex's global index
///     (sub-object base + local index) at weight 1 — the GBA morphs vertices
///     individually, so no blending exists to express.
/// </summary>
internal sealed class GbaSkinAssignment
{
    public required int SkeletonIndex { get; init; }

    /// <summary>Per-sub-object global vertex base: Σ VertCounts[0..sub).</summary>
    public required int[] SubObjectBoneBase { get; init; }

    public ModelBoneInfluences InfluenceOf(int subObject, int localIndex) =>
        ModelBoneInfluences.Single(SubObjectBoneBase[subObject] + localIndex);
}

/// <summary>
///     Builds a THPS2 GBA character as a 3D model: the shared morph-target skater
///     mesh (one static pose — the pool's frame 0, a neutral standing stance) with
///     the character's own outfit colours. Faces group into one primitive per
///     material, coloured with the mid shade of the material's palette ramp — the
///     runtime lights across the ramp; the mid shade is the authored colour
///     (verified: it dresses the skater anatomically — skin, shirt with logo,
///     pants, shoes, deck). Unlit, like the console's output.
///
///     <para>With a <see cref="GbaSkinAssignment" /> the same geometry binds to the
///     animated writer's bone-per-vertex rig; without one this is the plain static
///     export, byte-identical to before animation support existed. The packed
///     u16 normals stay undecoded (materials are unlit, so flat face normals
///     suffice).</para>
/// </summary>
internal static class GbaModelGeometryWriter
{
    /// <summary>Model-unit → GLB scale (the skater is ~77 s8 units tall; ×2 keeps
    ///     character models in the viewer's usual size range).</summary>
    public const float Scale = 2f;

    private const int StaticFrame = 0;

    public static void Populate(
        ModelDocument document, GbaModelNativeSource native, GbaSkinAssignment? skin = null)
    {
        var rom = native.Rom;
        var model = GbaSkaterModel.TryLocate(rom)
                    ?? throw new InvalidDataException("This ROM does not carry the skater model complex");

        var faces = GbaSkaterModel.ReadFaces(rom, model);
        var verts = GbaSkaterModel.ReadFrameVertices(rom, model, StaticFrame);
        var colors = GbaSkaterModel.TryGetMaterialColors(rom, model, native.CharacterIndex, native.Outfit)
                     ?? throw new InvalidDataException("The character's colour stream does not decode");

        var mesh = new ModelMesh { Name = native.CharacterName };

        // One primitive per used material, so each carries its authored colour.
        foreach (var group in faces.GroupBy(static f => f.Material).OrderBy(static g => g.Key))
        {
            var material = group.Key;
            var rgba = material < colors.Length ? colors[material] : [200, 200, 200, 255];
            var materialIndex = document.Materials.Count;
            document.Materials.Add(new RenderMaterial
            {
                Name = $"{native.CharacterName}_m{material:D2}",
                BaseColor = new Vector4(rgba[0] / 255f, rgba[1] / 255f, rgba[2] / 255f, 1f)
            });

            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            var influences = skin != null ? new List<ModelBoneInfluences>() : null;
            foreach (var face in group)
                AppendFace(vertices, indices, influences, skin, verts, face);

            var binding = influences == null
                ? null
                : new ModelSkinBinding
                {
                    SkeletonIndex = skin!.SkeletonIndex,
                    Influences = [.. influences]
                };
            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"m{material:D2}", materialIndex, vertices, indices, binding);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, native.CharacterName, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void AppendFace(
        List<ModelVertex> vertices, List<int> indices, List<ModelBoneInfluences>? influences,
        GbaSkinAssignment? skin, sbyte[][][] verts, GbaSkaterModel.Face face)
    {
        var sub = verts[face.SubObject];
        if (face.V0 >= sub.Length || face.V1 >= sub.Length || face.V2 >= sub.Length)
            return;
        var a = ToGlb(sub[face.V0]);
        var b = ToGlb(sub[face.V1]);
        var c = ToGlb(sub[face.V2]);
        var normal = Vector3.Cross(b - a, c - a);
        normal = normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal);
        var va = new ModelVertex(a, normal, Vector4.One, Vector2.Zero);
        var vb = new ModelVertex(b, normal, Vector4.One, Vector2.Zero);
        var vc = new ModelVertex(c, normal, Vector4.One, Vector2.Zero);
        if (influences != null)
        {
            ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                vertices, indices, influences,
                va, skin!.InfluenceOf(face.SubObject, face.V0),
                vb, skin.InfluenceOf(face.SubObject, face.V1),
                vc, skin.InfluenceOf(face.SubObject, face.V2));
        }
        else
        {
            ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, va, vb, vc);
        }
    }

    // Model space is z-up; GLB is y-up right-handed: (x, z, -y) preserves chirality.
    // Shared with GbaAnimatedModelWriter so bind pose and channel keys agree exactly.
    internal static Vector3 ToGlb(sbyte[] v) => new(v[0] * Scale, v[2] * Scale, -v[1] * Scale);
}
