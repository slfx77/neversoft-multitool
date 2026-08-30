using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Writes a DS collision world into a model document.
///
///     The surface is split by its raw surface word so a viewer can tell one terrain
///     from another, and each gameplay volume becomes its own mesh named by the id its
///     faces share. Both are the file's own triangles at the file's own positions —
///     nothing is generated.
///
///     <b>The edge network is not emitted.</b> It is a polyline, the document model
///     carries triangles only, and turning a segment into a ribbon would be inventing
///     geometry to look at rather than exporting what is there. It parses and is
///     available on <see cref="NdsCollisionFile.Edges" /> for a caller that wants it.
/// </summary>
public static class NdsCollisionWriter
{
    /// <summary>
    ///     Z-up to Y-up, the same basis the DS geometry writer applies, so a level's
    ///     collision lands on its render mesh rather than beside it.
    /// </summary>
    private static Vector3 ToGltf(in Vector3 v) => new(v.X, v.Z, -v.Y);

    public static void Populate(ModelDocument document, NdsCollisionFile collision)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(collision);

        var surfaces = new Dictionary<ushort, List<NdsCollisionFace>>();
        var volumes = new Dictionary<int, List<NdsCollisionFace>>();
        foreach (var face in collision.Faces)
        {
            var bucket = face.IsVolume
                ? volumes.TryGetValue(face.VolumeId, out var v) ? v : volumes[face.VolumeId] = []
                : surfaces.TryGetValue(face.Surface, out var s) ? s : surfaces[face.Surface] = [];
            bucket.Add(face);
        }

        foreach (var (surface, faces) in surfaces.OrderBy(s => s.Key))
            Emit(document, collision, faces, $"surface_{surface:x4}", IsOpaqueSurface: true);

        // Volumes are triggers rather than ground, so they carry their own material and
        // read as translucent shells instead of competing with the surface they sit on.
        foreach (var (id, faces) in volumes.OrderBy(v => v.Key))
            Emit(document, collision, faces, $"volume_{id:D4}", IsOpaqueSurface: false);

        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
    }

    private static void Emit(
        ModelDocument document,
        NdsCollisionFile collision,
        List<NdsCollisionFace> faces,
        string name,
        bool IsOpaqueSurface)
    {
        var vertices = new List<ModelVertex>(faces.Count * 3);
        var indices = new List<int>(faces.Count * 3);
        foreach (var face in faces)
        {
            var a = ToGltf(collision.Vertices[face.V0]);
            var b = ToGltf(collision.Vertices[face.V1]);
            var c = ToGltf(collision.Vertices[face.V2]);
            // Collision faces carry no normal, so one is derived per face — flat is
            // what a collision hull is, and a viewer needs something to shade with.
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
            var colour = IsOpaqueSurface ? Vector4.One : new Vector4(1f, 0.35f, 0.15f, 0.45f);
            foreach (var position in (ReadOnlySpan<Vector3>)[a, b, c])
            {
                indices.Add(vertices.Count);
                vertices.Add(new ModelVertex(position, normal, colour, Vector2.Zero));
            }
        }

        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = name,
            BaseColor = IsOpaqueSurface ? new Vector4(0.75f, 0.75f, 0.78f, 1f) : Vector4.One,
            AlphaMode = IsOpaqueSurface ? ModelAlphaMode.Opaque : ModelAlphaMode.Blend
        });

        var mesh = new ModelMesh { Name = name };
        ModelDocumentGeometryAdapter.AddPrimitive(mesh, name, materialIndex, vertices, indices);
        ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
    }
}
