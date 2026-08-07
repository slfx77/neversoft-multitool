using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     The plane/overlap math shared by every coplanar-decal detector, in plain
///     <see cref="Vector3" />/<see cref="Vector2" /> terms with no format types.
///     <para>
///         Neversoft authored decals as faces sitting exactly on the surface
///         they mark, because the PS1 has no z-buffer and sequences them through
///         the ordering table. The same authored geometry ships in the N64 ports
///         (where the RDP resolves it with a decal render mode) and in the Xbox
///         DDM decal ranks, so more than one detector needs the same answer to
///         "do these two coplanar faces really cover each other". This is that
///         answer, in one place — the constants and the exact clipping rule were
///         paid for once against the PS1 corpus and must not be re-derived per
///         format.
///     </para>
///     <para>
///         Lifted verbatim out of <see cref="PsxCoplanarOverlayDetector" />
///         (2026-08-07); its behaviour is unchanged and pinned by
///         <c>PsxCoplanarOverlayCensusTests</c>' nine per-file counts.
///         <see cref="BoundsOverlap" />'s two tolerances become parameters
///         because they are calibrated in WORLD UNITS against the authoring grid
///         — the PS1 grid step is 1/2.25, while an N64 build stores
///         <c>trunc(PS1raw / k)</c> and needs them scaled.
///     </para>
/// </summary>
internal static class CoplanarOverlayGeometry
{
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
    ///     True when two coplanar faces share enough area to be a real overlay
    ///     rather than neighbours touching along an edge.
    /// </summary>
    internal static bool HasInteriorOverlap(Vector3[] first, Vector3[] second)
    {
        return CoplanarSharedAreaFraction(first, second) >= MinimumSharedAreaFraction;
    }

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
    ///     A three-point face (every N64 face, and PSX triangles) takes the
    ///     first branch of <see cref="ProjectRenderedTriangles" /> alone, and
    ///     <see cref="EnsureCounterClockwise" /> normalises orientation before
    ///     clipping, so winding never reaches the result.
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
    ///     Per-axis AABB penetration test. A coplanar pair has ~zero depth along
    ///     the plane normal, so this demands REAL overlap on at least two axes
    ///     (the in-plane ones) and non-separation on the third. Edge-adjacent
    ///     panels touch with ~zero in-plane penetration and stay independent
    ///     (retail levels tile walls with alternating sign panels — lifting
    ///     those produced visible floating panels), while offset tile grids
    ///     (SKB2's two water sheets) genuinely interpenetrate.
    ///     <para>
    ///         Both tolerances are in WORLD UNITS and belong to the caller's
    ///         authoring grid, which is why they are parameters: the PS1 values
    ///         (0.1 / 0.25) are fractions of its 1/2.25 grid step.
    ///     </para>
    /// </summary>
    internal static bool BoundsOverlap(
        (Vector3 Min, Vector3 Max) first,
        (Vector3 Min, Vector3 Max) second,
        float touchTolerance,
        float realOverlap)
    {
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

    /// <summary>
    ///     The axis to drop when projecting a face into its plane's 2D basis —
    ///     the one its normal points most strongly along.
    /// </summary>
    internal static int DominantPlaneAxis(Vector3[] points)
    {
        var normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
        var x = MathF.Abs(normal.X);
        var y = MathF.Abs(normal.Y);
        var z = MathF.Abs(normal.Z);
        if (x >= y && x >= z) return 0;
        return y >= z ? 1 : 2;
    }

    /// <summary>
    ///     Canonicalizes a normal's sign so a plane and its back face share one
    ///     bucket key. Callers keep the returned sign to tell the two apart.
    /// </summary>
    internal static float FirstSignificantComponent(Vector3 value)
    {
        if (MathF.Abs(value.X) > 1e-6f) return value.X;
        if (MathF.Abs(value.Y) > 1e-6f) return value.Y;
        return value.Z;
    }

    /// <summary>
    ///     Projects a face into the plane's 2D basis as the renderer's triangles:
    ///     (0,2,1) plus (1,2,3) for a quad, (0,2,1) for a triangle — the same
    ///     winding <c>PsxGeometryWriter.AddPsxFace</c> emits. Returns the vertex
    ///     count written (3 or 6).
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
}
