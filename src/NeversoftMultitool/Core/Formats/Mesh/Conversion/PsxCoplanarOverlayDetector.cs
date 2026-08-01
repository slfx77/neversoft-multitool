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
        var planes = CollectPlanes(file);

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

    /// <summary>
    ///     Finds semi-transparent faces that need ONE extra lift step because
    ///     another semi-transparent layer of a different texture occupies the
    ///     same spot on the same plane (SKB2's animated wave sheet over its
    ///     static water sheet). Both layers get the standard one-step lift in
    ///     the writer, so without a tie-break they stay coplanar and z-fight.
    ///     Overlap is decided PER FACE PAIR (in-plane AABB intersection), never
    ///     from group union bounds — union bounds chain side-by-side sign
    ///     panels and scattered decals into deep stacks (retail lda1_g's wall
    ///     of 9 alternating panels floated 2+ units under the union model).
    ///     Only the actually-overlapping faces of the TOP layer lift, and the
    ///     extra lift is capped at one step. The top layer of a pair is the
    ///     sole texture-wibble-bound one when exactly one animates (a
    ///     scrolling overlay is authored to sit on its base); otherwise the
    ///     group with the lowest (object, face) key wins — the PS1 ordering
    ///     table PREPENDS primitives into a bucket and draws the bucket
    ///     head-first, so the first-inserted face paints LAST at equal depth,
    ///     i.e. on top.
    /// </summary>
    internal static IReadOnlyDictionary<PsxFaceInstanceKey, int> FindSemiTransparentLayerSteps(
        PsxMeshFile file)
    {
        Dictionary<PsxFaceInstanceKey, int>? steps = null;
        foreach (var candidates in CollectPlanes(file).Values)
        {
            var layerGroups = candidates
                .Where(static candidate => candidate.Face.IsSemiTransparent)
                .GroupBy(static candidate => (candidate.Face.TextureHash, candidate.Face.IsTextured))
                .Select(static group => group.ToList())
                .ToList();
            if (layerGroups.Count < 2)
                continue;

            for (var i = 0; i < layerGroups.Count; i++)
            {
                for (var j = i + 1; j < layerGroups.Count; j++)
                {
                    var top = SelectTopLayer(layerGroups[i], layerGroups[j]);
                    var bottom = ReferenceEquals(top, layerGroups[i]) ? layerGroups[j] : layerGroups[i];
                    foreach (var candidate in top.Where(candidate => bottom.Any(other =>
                                 BoundsOverlap((candidate.Min, candidate.Max), (other.Min, other.Max)))))
                    {
                        steps ??= [];
                        steps[candidate.Key] = 2;
                    }
                }
            }
        }

        return steps ?? (IReadOnlyDictionary<PsxFaceInstanceKey, int>)EmptySteps;
    }

    private static List<Candidate> SelectTopLayer(List<Candidate> first, List<Candidate> second)
    {
        var firstAnimated = first.Any(static candidate => candidate.Face.TextureWibble != null);
        var secondAnimated = second.Any(static candidate => candidate.Face.TextureWibble != null);
        if (firstAnimated != secondAnimated)
            return firstAnimated ? first : second;

        var firstKey = first.Min(static candidate => (candidate.Key.ObjectIndex, candidate.Key.FaceIndex));
        var secondKey = second.Min(static candidate => (candidate.Key.ObjectIndex, candidate.Key.FaceIndex));
        return firstKey.CompareTo(secondKey) <= 0 ? first : second;
    }

    private static readonly Dictionary<PsxFaceInstanceKey, int> EmptySteps = [];

    private static Dictionary<PlaneKey, List<Candidate>> CollectPlanes(PsxMeshFile file)
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

        return planes;
    }

    private static bool IsExactTwin(Candidate first, Candidate second)
    {
        const float epsilon = 0.01f;
        return Vector3.Distance(first.Min, second.Min) < epsilon
               && Vector3.Distance(first.Max, second.Max) < epsilon
               && MathF.Abs(first.Area - second.Area) < first.Area * 0.001f + epsilon;
    }

    private static bool BoundsOverlap(
        (Vector3 Min, Vector3 Max) first,
        (Vector3 Min, Vector3 Max) second)
    {
        // Per-axis penetration. A coplanar pair has ~zero depth along the
        // plane normal, so demand REAL overlap on at least two axes (the
        // in-plane ones) and non-separation on the third. Edge-adjacent
        // panels touch with ~zero in-plane penetration and stay independent
        // (retail levels tile walls with alternating sign panels — lifting
        // those produced visible floating panels), while offset tile grids
        // (SKB2's two water sheets) genuinely interpenetrate.
        const float touchTolerance = 0.1f;
        const float realOverlap = 0.25f;
        var overlapAxes = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            var penetration = MathF.Min(first.Max[axis], second.Max[axis])
                              - MathF.Max(first.Min[axis], second.Min[axis]);
            if (penetration < -touchTolerance)
                return false;
            if (penetration > realOverlap)
                overlapAxes++;
        }

        return overlapAxes >= 2;
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
        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        candidate = new Candidate(key, face, points, area, centroid, min, max);
        return true;
    }

    private static void ClassifyPair(
        Candidate first,
        Candidate second,
        HashSet<PsxFaceInstanceKey> overlays)
    {
        if (first.Face.TextureHash == second.Face.TextureHash
            && first.Face.IsTextured == second.Face.IsTextured
            && (IsExactTwin(first, second) || !BoundsOverlap((first.Min, first.Max), (second.Min, second.Max))))
        {
            // Same texture: exact whole-face twins (duplicated objects draw
            // the identical fragments — stable, no shimmer) and edge-adjacent
            // tiling stay untouched. Same-texture DIFFERENT-shape overlaps
            // (l2a1's start rooftop patches its gravel with offset quads of
            // the same texture) z-fight like any other pair and fall through.
            return;
        }

        var smaller = first.Area <= second.Area ? first : second;
        var larger = ReferenceEquals(smaller, first) ? second : first;

        // EITHER member being semi-transparent ends the pair here: every
        // semi-transparent face lifts along its position-averaged normal in
        // PsxGeometryWriter.AddPsxFace (see the lift block there), so the pair
        // is already separated GEOMETRICALLY and a draw-order flag on top would
        // double-resolve it. This deliberately diverges from the pre-2026-07-29
        // rule, which tested only the SMALLER member and therefore flagged the
        // opaque partner of a semi-transparent overlay: SKB2.PSX 23 -> 1 flags,
        // skware 34 -> 37, skmar/skmar_2/SKMAR -9 each, lda1_g -2 (several at
        // ~1.0 shared-area ratio). The counts are pinned per file by
        // PsxCoplanarOverlayCensusTests so the change cannot be silent again.
        // NOT yet established: that the lift always carries the semi-transparent
        // face to the FRONT of what it covers rather than into it (see the OPEN
        // note in CLAUDE.md).
        if (smaller.Face.IsSemiTransparent || larger.Face.IsSemiTransparent)
            return;

        if (smaller.Area < larger.Area * 0.95f)
        {
            // The classic decal: a clearly smaller face authored on a base.
            if (PointInsideFace(smaller.Centroid, larger.Points))
                overlays.Add(smaller.Key);
            return;
        }

        // Near-equal footprints: baked light/shadow duplicates — retail skmar
        // ships floor sections twice, day texture + shadowed texture on the
        // identical plane (4 such pairs per build), which z-fight as stripes.
        // No size cue exists, so use the PS1 ordering-table rule: the bucket
        // PREPENDS primitives and draws head-first, so the earliest-inserted
        // face paints LAST at equal depth, i.e. on top — that face becomes the
        // overlay.
        //
        // Real INTERIOR overlap is required, measured EXACTLY (2026-07-30).
        // AABB penetration alone passes for diagonal faces that merely share an
        // edge: on l2a1_g 296 same-texture pairs reach this branch and 266 of
        // them have zero polygon intersection, which is how the AABB-only rule
        // flagged 319 faces there (294-312 of them false). A centroid-inside
        // test removes those but silently drops PARTIAL overlaps where neither
        // centroid lands inside its partner — THPS2 DC SKPH.PSX loses 5 real
        // pairs (up to 0.32 of the smaller face, 5,653 units^2) and THPS1
        // skmall.psx 2 more (0.23, 22,595 units^2), regions that then z-fight.
        // So clip the two coplanar polygons against each other and require a
        // meaningful shared area instead: exact for both failure classes.
        if (!BoundsOverlap((first.Min, first.Max), (second.Min, second.Max))
            || !HasInteriorOverlap(first, second))
        {
            return;
        }

        var firstKey = (first.Key.ObjectIndex, first.Key.FaceIndex);
        var secondKey = (second.Key.ObjectIndex, second.Key.FaceIndex);
        overlays.Add(firstKey.CompareTo(secondKey) <= 0 ? first.Key : second.Key);
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

    /// <summary>
    ///     Smallest shared area that counts as a real overlap rather than an
    ///     edge-adjacency artifact, as a fraction of the smaller face. Edge- and
    ///     corner-adjacent faces clip to zero area, or to a float-noise sliver
    ///     — hence a floor rather than <c>&gt; 0</c>; the genuine partial
    ///     overlaps this must catch start at 19% of the smaller face (THPS1
    ///     skmall 0.23, THPS2 DC SKPH 0.32).
    /// </summary>
    internal const float MinimumSharedAreaFraction = 0.01f;

    /// <summary>
    ///     Exact shared area of two coplanar faces, as a fraction of the smaller
    ///     one (0 when they only touch along an edge or a corner).
    ///
    ///     Both faces decompose into the SAME two triangles the renderer draws
    ///     — (0,2,1) and (1,2,3) in PSX strip order — and the shared area is the
    ///     sum over triangle pairs. Clipping the quad as one polygon is invalid:
    ///     Sutherland-Hodgman requires a CONVEX clip polygon, and a strip-order
    ///     quad that is concave or non-planar walks a self-intersecting
    ///     perimeter, which silently returns ~0 for pairs that in fact overlap
    ///     almost completely (found 2026-07-31 — the earlier single-polygon
    ///     implementation asserted convexity without establishing it).
    ///     Triangles are convex and planar by construction, so the precondition
    ///     now holds for every face the format can express.
    ///
    ///     Shared by the near-equal overlay branch and the unit tests, so there
    ///     is one implementation of the geometry rather than two.
    /// </summary>
    internal static float CoplanarSharedAreaFraction(Vector3[] first, Vector3[] second)
    {
        var droppedAxis = DominantPlaneAxis(first);
        Span<Vector2> firstTriangles = stackalloc Vector2[6];
        Span<Vector2> secondTriangles = stackalloc Vector2[6];
        var firstCount = ProjectRenderedTriangles(first, droppedAxis, firstTriangles);
        var secondCount = ProjectRenderedTriangles(second, droppedAxis, secondTriangles);

        var firstArea = TriangleSetArea(firstTriangles, firstCount);
        var secondArea = TriangleSetArea(secondTriangles, secondCount);
        var smallerArea = MathF.Min(firstArea, secondArea);
        if (smallerArea <= 1e-4f)
            return 0f;

        var shared = 0f;
        for (var i = 0; i < firstCount; i += 3)
        {
            for (var j = 0; j < secondCount; j += 3)
            {
                var a = firstTriangles.Slice(i, 3);
                var b = secondTriangles.Slice(j, 3);
                EnsureCounterClockwise(a);
                EnsureCounterClockwise(b);
                shared += ConvexIntersectionArea(a, b);
            }
        }

        // Clamp to a valid proportion. A face whose own two rendered triangles
        // OVERLAP each other (a self-overlapping slot order) contributes its
        // shared region through more than one triangle pair, which would
        // otherwise report more than 100% of the smaller face — a fraction the
        // quantity is not defined to produce, and one that flowed into the
        // >= threshold comparison and the diagnostics as if it were meaningful.
        return MathF.Min(shared / smallerArea, 1f);
    }

    /// <summary>
    ///     Projects a face into the plane's 2D basis as the renderer's triangles:
    ///     (0,2,1) plus (1,2,3) for a quad, (0,2,1) for a triangle — the same
    ///     decomposition <see cref="PointInsideFace" /> uses and the same winding
    ///     <c>PsxGeometryWriter.AddPsxFace</c> emits. Returns the vertex count
    ///     written (3 or 6).
    /// </summary>
    private static int ProjectRenderedTriangles(Vector3[] points, int droppedAxis, Span<Vector2> destination)
    {
        destination[0] = ProjectPoint(points[0], droppedAxis);
        destination[1] = ProjectPoint(points[2], droppedAxis);
        destination[2] = ProjectPoint(points[1], droppedAxis);
        if (points.Length < 4)
            return 3;

        destination[3] = ProjectPoint(points[1], droppedAxis);
        destination[4] = ProjectPoint(points[2], droppedAxis);
        destination[5] = ProjectPoint(points[3], droppedAxis);
        return 6;
    }

    private static float TriangleSetArea(ReadOnlySpan<Vector2> triangles, int count)
    {
        var total = 0f;
        for (var i = 0; i < count; i += 3)
            total += PolygonArea(triangles.Slice(i, 3));
        return total;
    }

    private static bool HasInteriorOverlap(Candidate first, Candidate second)
    {
        return CoplanarSharedAreaFraction(first.Points, second.Points)
               >= MinimumSharedAreaFraction;
    }

    private static float ConvexIntersectionArea(
        ReadOnlySpan<Vector2> subject,
        ReadOnlySpan<Vector2> clip)
    {
        Span<Vector2> current = stackalloc Vector2[16];
        Span<Vector2> next = stackalloc Vector2[16];
        subject.CopyTo(current);
        var count = subject.Length;

        for (var edge = 0; edge < clip.Length && count >= 3; edge++)
        {
            var start = clip[edge];
            var end = clip[(edge + 1) % clip.Length];
            count = ClipAgainstEdge(current[..count], start, end, next);
            next[..count].CopyTo(current);
        }

        return count < 3 ? 0f : PolygonArea(current[..count]);
    }

    private static int ClipAgainstEdge(
        ReadOnlySpan<Vector2> input,
        Vector2 edgeStart,
        Vector2 edgeEnd,
        Span<Vector2> output)
    {
        var count = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var currentPoint = input[i];
            var nextPoint = input[(i + 1) % input.Length];
            var currentSide = SideOfEdge(edgeStart, edgeEnd, currentPoint);
            var nextSide = SideOfEdge(edgeStart, edgeEnd, nextPoint);

            if (currentSide >= 0f && count < output.Length)
                output[count++] = currentPoint;

            if (currentSide >= 0f == nextSide >= 0f)
                continue;

            var span = currentSide - nextSide;
            if (MathF.Abs(span) < 1e-12f || count >= output.Length)
                continue;

            output[count++] = Vector2.Lerp(currentPoint, nextPoint, currentSide / span);
        }

        return count;
    }

    private static float SideOfEdge(Vector2 edgeStart, Vector2 edgeEnd, Vector2 point)
    {
        var edge = edgeEnd - edgeStart;
        var offset = point - edgeStart;
        return edge.X * offset.Y - edge.Y * offset.X;
    }

    private static float PolygonArea(ReadOnlySpan<Vector2> polygon)
    {
        var doubled = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Length];
            doubled += current.X * next.Y - next.X * current.Y;
        }

        return MathF.Abs(doubled) * 0.5f;
    }

    private static void EnsureCounterClockwise(Span<Vector2> polygon)
    {
        var doubled = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Length];
            doubled += current.X * next.Y - next.X * current.Y;
        }

        if (doubled >= 0f)
            return;

        for (int head = 0, tail = polygon.Length - 1; head < tail; head++, tail--)
            (polygon[head], polygon[tail]) = (polygon[tail], polygon[head]);
    }

    private static Vector2 ProjectPoint(Vector3 point, int droppedAxis)
    {
        return droppedAxis switch
        {
            0 => new Vector2(point.Y, point.Z),
            1 => new Vector2(point.X, point.Z),
            _ => new Vector2(point.X, point.Y),
        };
    }

    private static int DominantPlaneAxis(Vector3[] points)
    {
        var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
        var x = MathF.Abs(normal.X);
        var y = MathF.Abs(normal.Y);
        var z = MathF.Abs(normal.Z);
        if (x >= y && x >= z) return 0;
        return y >= z ? 1 : 2;
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
        Vector3 Centroid,
        Vector3 Min,
        Vector3 Max);
}
