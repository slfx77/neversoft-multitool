using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

internal static class ThawPs2ReplayTriangleDiagnostics
{
    internal static List<DiagnosticTriangle> BuildPcTriangleDiagnostics(ParsedXbxScene scene)
    {
        var triangles = new List<DiagnosticTriangle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var sectorIndex = 0; sectorIndex < scene.Sectors.Length; sectorIndex++)
        {
            var sector = scene.Sectors[sectorIndex];
            for (var meshIndex = 0; meshIndex < sector.Meshes.Length; meshIndex++)
            {
                var mesh = sector.Meshes[meshIndex];
                for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
                {
                    var a = mesh.Vertices[mesh.FaceIndices[i]].Position;
                    var b = mesh.Vertices[mesh.FaceIndices[i + 1]].Position;
                    var c = mesh.Vertices[mesh.FaceIndices[i + 2]].Position;
                    if (!TryCreateTriangle(
                            mesh.MaterialChecksum,
                            a,
                            b,
                            c,
                            sectorIndex,
                            meshIndex,
                            -1,
                            -1,
                            -1,
                            -1,
                            out var triangle))
                    {
                        continue;
                    }

                    if (!seen.Add(GetTriangleKey(mesh.MaterialChecksum, triangle.A, triangle.B, triangle.C)))
                        continue;

                    triangles.Add(triangle);
                }
            }
        }

        return triangles;
    }

    internal static List<DiagnosticTriangle> BuildPs2TriangleDiagnostics(
        IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> kicks)
    {
        var triangles = new List<DiagnosticTriangle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kick in kicks)
        {
            foreach (var mesh in kick.Meshes)
            {
                var verts = mesh.Vertices;
                var stripStart = 0;
                var parityBias = mesh.StartsOnOddOutputSlot ? 1 : 0;

                for (var i = 0; i < verts.Length; i++)
                {
                    if (verts[i].IsStripRestart)
                        continue;

                    if (i - stripStart < 2)
                        continue;

                    Vector3 a;
                    Vector3 b;
                    Vector3 c;
                    if (((i - stripStart + parityBias) & 1) == 0)
                    {
                        a = verts[i - 2].Position;
                        b = verts[i - 1].Position;
                        c = verts[i].Position;
                    }
                    else
                    {
                        a = verts[i - 1].Position;
                        b = verts[i - 2].Position;
                        c = verts[i].Position;
                    }

                    if (!TryCreateTriangle(
                            mesh.MaterialChecksum,
                            a,
                            b,
                            c,
                            -1,
                            -1,
                            kick.KickIndex,
                            kick.SetupIndex,
                            kick.BatchIndex,
                            kick.EntryIndex,
                            out var triangle))
                    {
                        continue;
                    }

                    if (!seen.Add(GetTriangleKey(mesh.MaterialChecksum, triangle.A, triangle.B, triangle.C)))
                        continue;

                    triangles.Add(triangle);
                }
            }
        }

        return triangles;
    }

    internal static TriangleMatchResult EvaluateTriangleMatch(
        DiagnosticTriangle source,
        IReadOnlyList<DiagnosticTriangle> ps2Triangles,
        ILookup<uint, DiagnosticTriangle> ps2TrianglesByMaterial)
    {
        var global = FindBestTriangleMatch(source, ps2Triangles);
        var sameMaterialCandidates = ps2TrianglesByMaterial[source.MaterialChecksum].ToArray();
        var sameMaterial = sameMaterialCandidates.Length > 0
            ? FindBestTriangleMatch(source, sameMaterialCandidates)
            : null;

        return new TriangleMatchResult(source, global, sameMaterial);
    }

    internal static bool IsHeuristicallyUnmatched(TriangleMatchResult match)
    {
        return match.GlobalMatch.Metrics.AverageVertexDistance > 0.25f ||
               match.GlobalMatch.Metrics.MaxVertexDistance > 0.50f;
    }

    internal static List<List<TriangleMatchResult>> ClusterTriangleMatches(IReadOnlyList<TriangleMatchResult> matches)
    {
        var clusters = new List<List<TriangleMatchResult>>();
        if (matches.Count == 0)
            return clusters;

        var visited = new bool[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            if (visited[i])
                continue;

            var cluster = new List<TriangleMatchResult>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var current = matches[index];
                cluster.Add(current);

                for (var candidateIndex = 0; candidateIndex < matches.Count; candidateIndex++)
                {
                    if (visited[candidateIndex] ||
                        !AreAdjacentTriangles(current.Source, matches[candidateIndex].Source))
                    {
                        continue;
                    }

                    visited[candidateIndex] = true;
                    queue.Enqueue(candidateIndex);
                }
            }

            clusters.Add(cluster);
        }

        return clusters
            .OrderByDescending(cluster => cluster.Count)
            .ThenByDescending(cluster => cluster.Max(match => match.GlobalMatch.Metrics.Score))
            .ToList();
    }

    internal static TriangleBounds GetTriangleBounds(IEnumerable<DiagnosticTriangle> triangles)
    {
        using var enumerator = triangles.GetEnumerator();
        if (!enumerator.MoveNext())
            return new TriangleBounds(Vector3.Zero, Vector3.Zero);

        var first = enumerator.Current;
        var min = Vector3.Min(first.A, Vector3.Min(first.B, first.C));
        var max = Vector3.Max(first.A, Vector3.Max(first.B, first.C));

        while (enumerator.MoveNext())
        {
            var triangle = enumerator.Current;
            min = Vector3.Min(min, Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C)));
            max = Vector3.Max(max, Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)));
        }

        return new TriangleBounds(min, max);
    }

    internal static string FormatVector(Vector3 value)
    {
        return $"({value.X:F3},{value.Y:F3},{value.Z:F3})";
    }

    internal static string FormatMetric(float value)
    {
        return value.ToString("F5", CultureInfo.InvariantCulture);
    }

    internal static string FormatTriangle(DiagnosticTriangle triangle)
    {
        return $"{FormatVector(triangle.A)}|{FormatVector(triangle.B)}|{FormatVector(triangle.C)}";
    }

    internal static (Vector3, Vector3, Vector3) SortedTriKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);
    }

    private static TriangleSurfaceMatch FindBestTriangleMatch(
        DiagnosticTriangle source,
        IReadOnlyList<DiagnosticTriangle> candidates)
    {
        var bestTarget = candidates[0];
        var bestMetrics = MeasureTriangleMatch(source, bestTarget);

        for (var i = 1; i < candidates.Count; i++)
        {
            var currentTarget = candidates[i];
            var currentMetrics = MeasureTriangleMatch(source, currentTarget);
            if (!IsBetterMatch(currentMetrics, currentTarget, bestMetrics, bestTarget))
                continue;

            bestTarget = currentTarget;
            bestMetrics = currentMetrics;
        }

        return new TriangleSurfaceMatch(bestTarget, bestMetrics);
    }

    private static TriangleMatchMetrics MeasureTriangleMatch(DiagnosticTriangle source, DiagnosticTriangle target)
    {
        var distanceA = PointTriangleDistance(source.A, target.A, target.B, target.C);
        var distanceB = PointTriangleDistance(source.B, target.A, target.B, target.C);
        var distanceC = PointTriangleDistance(source.C, target.A, target.B, target.C);
        var averageVertexDistance = (distanceA + distanceB + distanceC) / 3f;
        var maxVertexDistance = Math.Max(distanceA, Math.Max(distanceB, distanceC));
        var centroidDistance = PointTriangleDistance(source.Centroid, target.A, target.B, target.C);
        var normalDot = Math.Abs(Vector3.Dot(source.Normal, target.Normal));
        if (float.IsNaN(normalDot))
            normalDot = 0f;

        var areaRatio = source.Area <= 0f || target.Area <= 0f
            ? 0f
            : Math.Min(source.Area, target.Area) / Math.Max(source.Area, target.Area);

        var score = averageVertexDistance + 0.25f * centroidDistance + 0.10f * (1f - normalDot);
        return new TriangleMatchMetrics(
            score,
            averageVertexDistance,
            maxVertexDistance,
            centroidDistance,
            normalDot,
            areaRatio);
    }

    private static bool IsBetterMatch(
        TriangleMatchMetrics currentMetrics,
        DiagnosticTriangle currentTarget,
        TriangleMatchMetrics bestMetrics,
        DiagnosticTriangle bestTarget)
    {
        if (currentMetrics.Score < bestMetrics.Score - 1e-5f)
            return true;
        if (currentMetrics.Score > bestMetrics.Score + 1e-5f)
            return false;
        if (currentMetrics.MaxVertexDistance < bestMetrics.MaxVertexDistance - 1e-5f)
            return true;
        if (currentMetrics.MaxVertexDistance > bestMetrics.MaxVertexDistance + 1e-5f)
            return false;
        if (currentMetrics.NormalDot > bestMetrics.NormalDot + 1e-5f)
            return true;
        if (currentMetrics.NormalDot < bestMetrics.NormalDot - 1e-5f)
            return false;
        if (currentMetrics.AreaRatio > bestMetrics.AreaRatio + 1e-5f)
            return true;
        if (currentMetrics.AreaRatio < bestMetrics.AreaRatio - 1e-5f)
            return false;

        return Compare(currentTarget.Centroid, bestTarget.Centroid) < 0;
    }

    private static bool AreAdjacentTriangles(DiagnosticTriangle left, DiagnosticTriangle right)
    {
        if (left.MaterialChecksum != right.MaterialChecksum)
            return false;

        return SharesVertex(left, right) ||
               Vector3.DistanceSquared(left.Centroid, right.Centroid) <= 1.0f;
    }

    private static bool SharesVertex(DiagnosticTriangle left, DiagnosticTriangle right)
    {
        const float epsilon = 1e-6f;
        return SamePoint(left.A, right.A, epsilon) ||
               SamePoint(left.A, right.B, epsilon) ||
               SamePoint(left.A, right.C, epsilon) ||
               SamePoint(left.B, right.A, epsilon) ||
               SamePoint(left.B, right.B, epsilon) ||
               SamePoint(left.B, right.C, epsilon) ||
               SamePoint(left.C, right.A, epsilon) ||
               SamePoint(left.C, right.B, epsilon) ||
               SamePoint(left.C, right.C, epsilon);
    }

    private static bool SamePoint(Vector3 left, Vector3 right, float epsilon)
    {
        return Vector3.DistanceSquared(left, right) <= epsilon;
    }

    private static string GetTriangleKey(uint materialChecksum, Vector3 a, Vector3 b, Vector3 c)
    {
        var sorted = SortedTriKey(a, b, c);
        return
            $"{materialChecksum:X8}:{FormatKeyVector(sorted.Item1)}|{FormatKeyVector(sorted.Item2)}|{FormatKeyVector(sorted.Item3)}";
    }

    private static string FormatKeyVector(Vector3 value)
    {
        return $"{value.X:R}|{value.Y:R}|{value.Z:R}";
    }

    private static bool TryCreateTriangle(
        uint materialChecksum,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        int sectorIndex,
        int meshIndex,
        int kickIndex,
        int setupIndex,
        int batchIndex,
        int entryIndex,
        out DiagnosticTriangle triangle)
    {
        triangle = default!;
        if (a == b || b == c || a == c)
            return false;

        var cross = Vector3.Cross(b - a, c - a);
        var crossLength = cross.Length();
        if (crossLength <= 1e-6f)
            return false;

        var sorted = SortedTriKey(a, b, c);
        triangle = new DiagnosticTriangle(
            materialChecksum,
            sorted.Item1,
            sorted.Item2,
            sorted.Item3,
            (a + b + c) / 3f,
            cross / crossLength,
            crossLength * 0.5f,
            sectorIndex,
            meshIndex,
            kickIndex,
            setupIndex,
            batchIndex,
            entryIndex);
        return true;
    }

    private static float PointTriangleDistance(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = point - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
            return Vector3.Distance(point, a);

        var bp = point - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
            return Vector3.Distance(point, b);

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            var v = d1 / (d1 - d3);
            return Vector3.Distance(point, a + v * ab);
        }

        var cp = point - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
            return Vector3.Distance(point, c);

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            var w = d2 / (d2 - d6);
            return Vector3.Distance(point, a + w * ac);
        }

        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return Vector3.Distance(point, b + w * (c - b));
        }

        var normal = Vector3.Normalize(Vector3.Cross(ab, ac));
        return Math.Abs(Vector3.Dot(point - a, normal));
    }

    private static int Compare(Vector3 x, Vector3 y)
    {
        var cmp = x.X.CompareTo(y.X);
        if (cmp != 0)
            return cmp;

        cmp = x.Y.CompareTo(y.Y);
        return cmp != 0 ? cmp : x.Z.CompareTo(y.Z);
    }

    internal sealed record DiagnosticTriangle(
        uint MaterialChecksum,
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector3 Centroid,
        Vector3 Normal,
        float Area,
        int SectorIndex,
        int MeshIndex,
        int KickIndex,
        int SetupIndex,
        int BatchIndex,
        int EntryIndex);

    internal sealed record TriangleMatchMetrics(
        float Score,
        float AverageVertexDistance,
        float MaxVertexDistance,
        float CentroidDistance,
        float NormalDot,
        float AreaRatio);

    internal sealed record TriangleSurfaceMatch(
        DiagnosticTriangle Target,
        TriangleMatchMetrics Metrics);

    internal sealed record TriangleMatchResult(
        DiagnosticTriangle Source,
        TriangleSurfaceMatch GlobalMatch,
        TriangleSurfaceMatch? SameMaterialMatch);

    internal readonly record struct TriangleBounds(Vector3 Min, Vector3 Max);
}
