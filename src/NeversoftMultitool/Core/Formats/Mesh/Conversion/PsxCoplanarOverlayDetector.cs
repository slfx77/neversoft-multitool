using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Finds small opaque faces authored directly on top of a larger face.
///     The PS1 resolves these ordering-table overlays by draw order and has no
///     depth buffer; glTF viewers need a small geometric bias to avoid fighting.
/// </summary>
internal static class PsxCoplanarOverlayDetector
{
    internal static IReadOnlySet<PsxFaceInstanceKey> Find(PsxMeshFile file)
    {
        return FindGroups(file).Keys.ToHashSet();
    }

    /// <summary>
    ///     Like <see cref="Find" /> but grouped: every detected overlay face maps
    ///     to a deterministic per-plane group id, so the writer can emit each
    ///     coplanar overlay group as its own mesh with one rigid draw-order /
    ///     separation-vector metadata record (group ids are ordered by the
    ///     group's first face key).
    /// </summary>
    internal static IReadOnlyDictionary<PsxFaceInstanceKey, int> FindGroups(PsxMeshFile file)
    {
        var planes = new Dictionary<PlaneKey, List<Candidate>>();
        for (var objectIndex = 0; objectIndex < file.Objects.Count; objectIndex++)
        {
            var obj = file.Objects[objectIndex];
            if (obj.MeshIndex >= file.Meshes.Count)
                continue;

            var mesh = file.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(file, obj));
            for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
            {
                var face = mesh.Faces[faceIndex];
                if (!TryCreateCandidate(
                        new PsxFaceInstanceKey(objectIndex, faceIndex),
                        mesh,
                        face,
                        offset,
                        out var plane,
                        out var candidate))
                    continue;

                if (!planes.TryGetValue(plane, out var candidates))
                {
                    candidates = [];
                    planes.Add(plane, candidates);
                }

                candidates.Add(candidate);
            }
        }

        var planeGroups = new List<HashSet<PsxFaceInstanceKey>>();
        foreach (var candidates in planes.Values)
        {
            var overlays = new HashSet<PsxFaceInstanceKey>();
            for (var i = 0; i < candidates.Count; i++)
            {
                for (var j = i + 1; j < candidates.Count; j++)
                    ClassifyPair(candidates[i], candidates[j], overlays);
            }

            if (overlays.Count > 0)
                planeGroups.Add(overlays);
        }

        var groups = new Dictionary<PsxFaceInstanceKey, int>();
        var groupId = 0;
        foreach (var planeGroup in planeGroups
                     .OrderBy(static group => group.Min(static key => (key.ObjectIndex, key.FaceIndex))))
        {
            foreach (var key in planeGroup)
                groups[key] = groupId;
            groupId++;
        }

        return groups;
    }

    private static bool TryCreateCandidate(
        PsxFaceInstanceKey key,
        PsxMesh mesh,
        PsxFace face,
        Vector3 offset,
        out PlaneKey plane,
        out Candidate candidate)
    {
        plane = default;
        candidate = null!;
        var count = face.IsQuad ? 4 : 3;
        var points = new Vector3[count];
        for (var slot = 0; slot < count; slot++)
        {
            var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
            if (vertexIndex >= mesh.Vertices.Count)
                return false;
            var vertex = mesh.Vertices[(int)vertexIndex];
            points[slot] = new Vector3(vertex.X, -vertex.Y, -vertex.Z) + offset;
        }

        var cross = Vector3.Cross(points[2] - points[0], points[1] - points[0]);
        var twiceFirstArea = cross.Length();
        if (twiceFirstArea < 1e-5f)
            return false;

        var area = twiceFirstArea * 0.5f;
        if (face.IsQuad)
            area += Vector3.Cross(points[2] - points[1], points[3] - points[1]).Length() * 0.5f;

        var normal = cross / twiceFirstArea;
        if (FirstSignificantComponent(normal) < 0f)
            normal = -normal;
        var distance = Vector3.Dot(normal, points[0]);
        plane = new PlaneKey(
            (int)MathF.Round(normal.X * 1000f),
            (int)MathF.Round(normal.Y * 1000f),
            (int)MathF.Round(normal.Z * 1000f),
            (int)MathF.Round(distance * 100f));
        var centroid = points.Aggregate(Vector3.Zero, static (sum, point) => sum + point) / points.Length;
        candidate = new Candidate(key, face, points, area, centroid);
        return true;
    }

    private static void ClassifyPair(
        Candidate first,
        Candidate second,
        HashSet<PsxFaceInstanceKey> overlays)
    {
        if (first.Face.TextureHash == second.Face.TextureHash
            && first.Face.IsTextured == second.Face.IsTextured)
            return;

        var smaller = first.Area <= second.Area ? first : second;
        var larger = ReferenceEquals(smaller, first) ? second : first;
        if (smaller.Area >= larger.Area * 0.95f
            || smaller.Face.IsSemiTransparent
            || !PointInsideFace(smaller.Centroid, larger.Points))
            return;

        overlays.Add(smaller.Key);
    }

    private static bool PointInsideFace(Vector3 point, Vector3[] face)
    {
        return PointInsideTriangle(point, face[0], face[2], face[1])
               || (face.Length == 4 && PointInsideTriangle(point, face[1], face[2], face[3]));
    }

    private static bool PointInsideTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = point - a;
        var dot00 = Vector3.Dot(v0, v0);
        var dot01 = Vector3.Dot(v0, v1);
        var dot02 = Vector3.Dot(v0, v2);
        var dot11 = Vector3.Dot(v1, v1);
        var dot12 = Vector3.Dot(v1, v2);
        var denominator = dot00 * dot11 - dot01 * dot01;
        if (MathF.Abs(denominator) < 1e-8f)
            return false;

        var inverse = 1f / denominator;
        var u = (dot11 * dot02 - dot01 * dot12) * inverse;
        var v = (dot00 * dot12 - dot01 * dot02) * inverse;
        const float tolerance = 1e-4f;
        return u >= -tolerance && v >= -tolerance && u + v <= 1f + tolerance;
    }

    private static float FirstSignificantComponent(Vector3 value)
    {
        if (MathF.Abs(value.X) > 1e-6f) return value.X;
        if (MathF.Abs(value.Y) > 1e-6f) return value.Y;
        return value.Z;
    }

    private readonly record struct PlaneKey(int X, int Y, int Z, int Distance);

    private sealed record Candidate(
        PsxFaceInstanceKey Key,
        PsxFace Face,
        Vector3[] Points,
        float Area,
        Vector3 Centroid);
}
