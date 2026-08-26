using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds a THPS2 GBA character as a 3D model: the shared morph-target skater
///     mesh (one static pose — the pool's frame 0, a neutral standing stance) with
///     the character's own outfit colours. Faces group into one primitive per
///     material, coloured with the mid shade of the material's palette ramp — the
///     runtime lights across the ramp; the mid shade is the authored colour
///     (verified: it dresses the skater anatomically — skin, shirt with logo,
///     pants, shoes, deck). Unlit, like the console's output.
///
///     <para>Animation (221 clips of posed frames) and the packed-normal shading
///     are decoded in the research record but not yet exported — morph-target
///     animation support in the GLB pipeline is the follow-up.</para>
/// </summary>
internal static class GbaModelGeometryWriter
{
    /// <summary>Model-unit → GLB scale (the skater is ~77 s8 units tall; ×2 keeps
    ///     character models in the viewer's usual size range).</summary>
    public const float Scale = 2f;

    private const int StaticFrame = 0;

    public static void Populate(ModelDocument document, GbaModelNativeSource native)
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
            foreach (var face in group)
            {
                var sub = verts[face.SubObject];
                if (face.V0 >= sub.Length || face.V1 >= sub.Length || face.V2 >= sub.Length)
                    continue;
                var a = ToGlb(sub[face.V0]);
                var b = ToGlb(sub[face.V1]);
                var c = ToGlb(sub[face.V2]);
                var normal = Vector3.Cross(b - a, c - a);
                normal = normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal);
                ModelDocumentGeometryAdapter.AddTriangle(
                    vertices, indices,
                    new ModelVertex(a, normal, Vector4.One, Vector2.Zero),
                    new ModelVertex(b, normal, Vector4.One, Vector2.Zero),
                    new ModelVertex(c, normal, Vector4.One, Vector2.Zero));
            }

            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"m{material:D2}", materialIndex, vertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, native.CharacterName, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    // Model space is z-up; GLB is y-up right-handed: (x, z, -y) preserves chirality.
    private static Vector3 ToGlb(sbyte[] v) => new(v[0] * Scale, v[2] * Scale, -v[1] * Scale);
}
