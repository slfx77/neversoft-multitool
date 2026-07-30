using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace PsxAnalyzer.Commands;

/// <summary>
///     Groups a PSX file's faces into coplanar buckets using the SAME
///     quantization as <see cref="PsxCoplanarOverlayDetector" />, so diagnostics
///     see exactly the neighbour sets the detector reasons about. Positions are
///     authored-world glTF space ((x, -y, -z) plus the object offset), matching
///     both the detector and
///     <c>PsxGeometryWriter.BuildSemiTransparentLiftDirections</c>.
///     Shared by <c>overlay-census</c> and <c>st-lift-audit</c>.
/// </summary>
internal static class PsxAnalyzerPlanes
{
    internal sealed record FaceGeometry(
        PsxFaceInstanceKey Key,
        PsxFace Face,
        Vector3[] Points,
        Vector3 Centroid,
        Vector3 Min,
        Vector3 Max);

    internal static Dictionary<(int, int, int, int), List<FaceGeometry>> Collect(PsxMeshFile file)
    {
        var planes = new Dictionary<(int, int, int, int), List<FaceGeometry>>();
        for (var objectIndex = 0; objectIndex < file.Objects.Count; objectIndex++)
        {
            var obj = file.Objects[objectIndex];
            if (obj.MeshIndex >= file.Meshes.Count)
                continue;

            var mesh = file.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(PsxMeshSemantics.GetObjectOffset(file, obj));
            for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
            {
                var geometry = BuildFace(mesh, objectIndex, faceIndex, offset);
                if (geometry is null)
                    continue;

                var plane = PlaneKeyOf(geometry.Points);
                if (!planes.TryGetValue(plane, out var list))
                    planes[plane] = list = [];
                list.Add(geometry);
            }
        }

        return planes;
    }

    private static FaceGeometry? BuildFace(
        PsxMesh mesh,
        int objectIndex,
        int faceIndex,
        Vector3 offset)
    {
        var face = mesh.Faces[faceIndex];
        var count = face.IsQuad ? 4 : 3;
        var points = new Vector3[count];
        for (var slot = 0; slot < count; slot++)
        {
            var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
            if (vertexIndex >= mesh.Vertices.Count)
                return null;

            var vertex = mesh.Vertices[(int)vertexIndex];
            points[slot] = new Vector3(vertex.X, -vertex.Y, -vertex.Z) + offset;
        }

        var cross = Vector3.Cross(points[2] - points[0], points[1] - points[0]);
        if (cross.Length() < 1e-5f)
            return null;

        var centroid = points.Aggregate(Vector3.Zero, static (sum, point) => sum + point) / points.Length;
        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        return new FaceGeometry(new PsxFaceInstanceKey(objectIndex, faceIndex), face, points, centroid, min, max);
    }

    private static (int, int, int, int) PlaneKeyOf(Vector3[] points)
    {
        var cross = Vector3.Cross(points[2] - points[0], points[1] - points[0]);
        var normal = cross / cross.Length();
        if (FirstSignificantComponent(normal) < 0f)
            normal = -normal;

        return (
            (int)MathF.Round(normal.X * 1000f),
            (int)MathF.Round(normal.Y * 1000f),
            (int)MathF.Round(normal.Z * 1000f),
            (int)MathF.Round(Vector3.Dot(normal, points[0]) * 100f));
    }

    private static float FirstSignificantComponent(Vector3 value)
    {
        if (MathF.Abs(value.X) > 1e-6f) return value.X;
        if (MathF.Abs(value.Y) > 1e-6f) return value.Y;
        return value.Z;
    }
}
