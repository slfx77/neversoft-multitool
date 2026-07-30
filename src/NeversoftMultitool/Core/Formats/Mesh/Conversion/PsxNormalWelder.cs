using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Position-welded normals for PSX meshes that ship only per-FACE normals
///     (<see cref="PsxMesh.HasPerVertexNormals" /> false). The engine never lit
///     these meshes — they draw their authored flat/gouraud colours — but our
///     export assigns each corner its face's normal, so lit viewers and Blender
///     shade a hard facet seam along every edge (the SKBUL sky dome's 32
///     texture bands each became their own smoothing island because the
///     exporter-level <see cref="GltfNormalSmoother" /> works per primitive).
///     Welding averages the face normals of every face meeting at a position,
///     restricted to faces within ~60° of the querying face so authored hard
///     edges (boxes, letter outlines) stay crisp while domes shade smoothly.
/// </summary>
internal sealed class PsxNormalWelder
{
    // cos(60°): faces beyond this dihedral belong to a different smoothing
    // group and must not bend this corner's normal.
    private const float SmoothingDotThreshold = 0.5f;

    private readonly Dictionary<(int X, int Y, int Z), List<Vector3>> _normalsByPosition;

    private PsxNormalWelder(Dictionary<(int X, int Y, int Z), List<Vector3>> normalsByPosition)
    {
        _normalsByPosition = normalsByPosition;
    }

    /// <summary>
    ///     Builds the weld map from the mesh's stored per-face normals, keyed
    ///     by emitted-space corner position. Null when the mesh has per-vertex
    ///     normals (nothing to weld) or no usable face normals.
    /// </summary>
    internal static PsxNormalWelder? Build(PsxMesh mesh)
    {
        if (mesh.HasPerVertexNormals)
            return null;

        Dictionary<(int X, int Y, int Z), List<Vector3>>? map = null;
        foreach (var face in mesh.Faces)
        {
            if (face.NormalIndex >= mesh.Normals.Count)
                continue;

            var stored = mesh.Normals[(int)face.NormalIndex];
            var normal = ModelDocumentGeometryAdapter.NormalizeOrDefault(
                new Vector3(stored.X, -stored.Y, -stored.Z));

            var count = face.IsQuad ? 4 : 3;
            for (var slot = 0; slot < count; slot++)
            {
                var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
                if (vertexIndex >= mesh.Vertices.Count)
                    continue;

                var vertex = mesh.Vertices[(int)vertexIndex];
                var key = Quantize(new Vector3(vertex.X, -vertex.Y, -vertex.Z));
                map ??= [];
                if (!map.TryGetValue(key, out var list))
                {
                    list = [];
                    map[key] = list;
                }

                list.Add(normal);
            }
        }

        return map == null ? null : new PsxNormalWelder(map);
    }

    /// <summary>
    ///     The welded normal for a corner at <paramref name="position" /> on a
    ///     face whose own normal is <paramref name="faceNormal" /> — the
    ///     average of co-located face normals within the smoothing threshold,
    ///     or the face normal unchanged when nothing qualifies.
    /// </summary>
    internal Vector3 Resolve(Vector3 position, Vector3 faceNormal)
    {
        if (!_normalsByPosition.TryGetValue(Quantize(position), out var normals))
            return faceNormal;

        var sum = Vector3.Zero;
        foreach (var normal in normals.Where(n => Vector3.Dot(n, faceNormal) > SmoothingDotThreshold))
            sum += normal;

        var lengthSquared = sum.LengthSquared();
        return lengthSquared > 1e-12f
            ? sum / MathF.Sqrt(lengthSquared)
            : faceNormal;
    }

    private static (int X, int Y, int Z) Quantize(Vector3 position)
    {
        return ((int)MathF.Round(position.X * 64f),
            (int)MathF.Round(position.Y * 64f),
            (int)MathF.Round(position.Z * 64f));
    }
}
