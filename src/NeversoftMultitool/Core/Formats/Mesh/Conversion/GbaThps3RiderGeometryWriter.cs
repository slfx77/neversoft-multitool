using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds the THPS3 GBA rider as a 3D model: the rider part at the pool's
///     frame 0 plus the deck at frame 0's deck translation, faces grouped into one
///     primitive per material byte. Every corner carries its authored texture
///     coordinate (6.2 fixed-point texels of the library's 64×64 page, normalised
///     by 256), but the page itself is not located in the ROM, so the materials
///     are <b>diagnostic</b> colours keyed by the material byte, named so, with no
///     image. Normals are computed facets (the format stores none). With morph
///     targets the same base mesh carries one clip's distinct frames — see
///     <see cref="GbaThps3RiderAnimatedWriter" />.
/// </summary>
internal static class GbaThps3RiderGeometryWriter
{
    /// <summary>Model-unit → GLB scale (the rider is ~97 s8 units tall; ×2 keeps it
    ///     in the viewer's usual character range, matching the THPS2 skater).</summary>
    public const float Scale = 2f;

    public const string MeshName = "rider";

    private const int StaticFrame = 0;

    public static void Populate(
        ModelDocument document, GbaThps3RiderNativeSource native, GbaMorphTargets? morphTargets = null)
    {
        var rom = native.Rom;
        var model = GbaThps3RiderModel.TryLocate(rom)
                    ?? throw new InvalidDataException("This ROM does not carry the THPS3 rider complex");

        var faces = GbaThps3RiderModel.ReadFaces(rom, model);
        var pose = PoseOf(rom, model, StaticFrame);
        var normals = morphTargets == null ? null : AverageVertexNormals(faces, pose);

        var mesh = new ModelMesh { Name = MeshName };
        foreach (var group in faces.GroupBy(static f => f.Material).OrderBy(static g => g.Key))
        {
            var material = group.Key;
            var materialIndex = document.Materials.Count;
            document.Materials.Add(new RenderMaterial
            {
                Name = $"{MeshName}_m{material:D2}_debug",
                BaseColor = DiagnosticColour(material)
            });

            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            var sources = morphTargets == null ? null : new List<int>();
            foreach (var face in group)
                AppendFace(vertices, indices, sources, pose, normals, face);

            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"m{material:D2}", materialIndex, vertices, indices,
                morphTargets: sources == null ? null : morphTargets!.ForPrimitive(sources));
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, MeshName, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    /// <summary>
    ///     One frame's complete posed vertex set in GLB space, in the faces' global
    ///     numbering: the rider's part-0 vertices from the frame, then the deck's
    ///     stored vertices moved by that frame's proven header translation.
    /// </summary>
    internal static Vector3[] PoseOf(ReadOnlySpan<byte> rom, GbaThps3RiderModel.ModelInfo model, int frame)
    {
        var rider = GbaThps3RiderModel.ReadFrameVertices(rom, model, frame);
        var deck = GbaThps3RiderModel.ReadDeckVertices(rom, model);
        var (dx, dy, dz) = GbaThps3RiderModel.ReadFrameHeader(rom, model, frame).DeckTranslation;

        var pose = new Vector3[rider.Length + deck.Length];
        for (var i = 0; i < rider.Length; i++)
            pose[i] = ToGlb(rider[i][0], rider[i][1], rider[i][2]);
        for (var i = 0; i < deck.Length; i++)
            pose[rider.Length + i] = ToGlb(deck[i][0] + dx, deck[i][1] + dy, deck[i][2] + dz);
        return pose;
    }

    // Model space is z-up; GLB is y-up right-handed: (x, z, -y) preserves chirality.
    internal static Vector3 ToGlb(int x, int y, int z) => new(x * Scale, z * Scale, -y * Scale);

    /// <summary>A 6.2 fixed-point texel byte as a normalised page coordinate.</summary>
    internal static Vector2 ToTexCoord(GbaThps3RiderModel.TexCoord t) =>
        new(t.U / (4f * GbaThps3RiderModel.TexturePageSize), t.V / (4f * GbaThps3RiderModel.TexturePageSize));

    private static Vector4 DiagnosticColour(int material)
    {
        // Distinct, deterministic hues per material byte — visibly placeholders.
        var hue = material * 0.618034f % 1f;
        var (r, g, b) = HueToRgb(hue);
        return new Vector4(0.25f + 0.65f * r, 0.25f + 0.65f * g, 0.25f + 0.65f * b, 1f);
    }

    private static (float, float, float) HueToRgb(float hue)
    {
        var h = hue * 6f;
        var x = 1f - MathF.Abs(h % 2f - 1f);
        return (int)h switch
        {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x)
        };
    }

    private static void AppendFace(
        List<ModelVertex> vertices, List<int> indices, List<int>? sources,
        Vector3[] pose, Vector3[]? normals, GbaThps3RiderModel.Face face)
    {
        if (face.V0 >= pose.Length || face.V1 >= pose.Length || face.V2 >= pose.Length)
            return;
        var a = pose[face.V0];
        var b = pose[face.V1];
        var c = pose[face.V2];
        var faceNormal = Vector3.Cross(b - a, c - a);
        faceNormal = faceNormal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(faceNormal);

        var before = vertices.Count;
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices,
            new ModelVertex(a, normals?[face.V0] ?? faceNormal, Vector4.One, ToTexCoord(face.T0)),
            new ModelVertex(b, normals?[face.V1] ?? faceNormal, Vector4.One, ToTexCoord(face.T1)),
            new ModelVertex(c, normals?[face.V2] ?? faceNormal, Vector4.One, ToTexCoord(face.T2)));
        if (sources == null || vertices.Count == before)
            return; // a degenerate triangle the adapter dropped

        sources.Add(face.V0);
        sources.Add(face.V1);
        sources.Add(face.V2);
    }

    // Morph deltas are keyed by base-vertex geometry, so a source vertex must
    // resolve to ONE (position, normal) pair: average the facet normals per
    // source vertex (the same rule the THPS2 writer uses).
    private static Vector3[] AverageVertexNormals(List<GbaThps3RiderModel.Face> faces, Vector3[] pose)
    {
        var normals = new Vector3[pose.Length];
        foreach (var face in faces)
        {
            if (face.V0 >= pose.Length || face.V1 >= pose.Length || face.V2 >= pose.Length)
                continue;
            var a = pose[face.V0];
            var normal = Vector3.Cross(pose[face.V1] - a, pose[face.V2] - a);
            if (normal.LengthSquared() < 1e-12f)
                continue;
            normal = Vector3.Normalize(normal);
            normals[face.V0] += normal;
            normals[face.V1] += normal;
            normals[face.V2] += normal;
        }

        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normals[i]);
        return normals;
    }
}
