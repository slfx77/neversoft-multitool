using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>Identifies one triangle instance: the placing object plus its index within the node.</summary>
internal readonly record struct N64TriangleInstanceKey(int ObjectIndex, int TriangleIndex);

/// <summary>A flagged triangle's per-plane group and its draw rank within that group.</summary>
internal readonly record struct N64CoplanarOverlayAssignment(int GroupId, int DrawRank);

/// <summary>
///     One triangle offered to the detector, already in export space. The
///     ROM-facing overload builds these from a shell plus its placements; the
///     synthetic overload lets tests supply them directly.
/// </summary>
internal sealed record N64OverlayCandidateSource(
    N64TriangleInstanceKey Key,
    Vector3[] Points,
    int TextureSlot,
    ushort FaceFlags);

/// <summary>
///     Finds N64 decals authored exactly on the surface they mark.
///     <para>
///         The ports ship the PS1's authored geometry unchanged, and on the PS1
///         that means a decal sits precisely on its base — the console has no
///         depth buffer and sequences them through the ordering table. The N64
///         does have a z-buffer, and the RDP resolves the coincidence with a
///         decal render mode, so the geometry arrives coplanar either way and
///         z-fights once exported to glTF. Measured on THPS1: School has 107
///         same-facing coplanar overlapping pairs and its PS1 sibling
///         <c>skschl.psx</c> flags 393 faces.
///     </para>
///     <para>
///         The overlap rule itself is the PS1 detector's, shared verbatim
///         through <see cref="CoplanarOverlayGeometry" />. What differs is
///         everything format-specific: the candidates are triangles rather than
///         quads, their world offset is resolved PER CORNER (a triangle can
///         bridge two animation matrices), and the tolerances scale with the
///         build's own quantisation instead of being PS1 world-unit literals.
///     </para>
/// </summary>
internal static class N64CoplanarOverlayDetector
{
    /// <summary>
    ///     A face must be this much smaller than its partner to be read as a
    ///     decal on it rather than a co-equal layer. Same value the PS1
    ///     detector uses.
    /// </summary>
    private const float DecalAreaRatio = 0.95f;

    /// <summary>
    ///     Plane-distance bucket width, as a multiple of one raw N64 unit.
    ///     <para>
    ///         N64 vertex coordinates are s16 INTEGERS, so within a node two
    ///         authored-distinct planes differ by at least one raw unit — an
    ///         eighth of one cannot merge them. It is wide enough to absorb the
    ///         noise the PS1 never has: the per-corner offset is a float
    ///         division, positions reach ~10³, and a PS1 quad arrives as two
    ///         triangles whose normals are reconstructed from different corner
    ///         triples, which together move the plane distance by ~1e-3. The
    ///         PS1 detector's fixed 0.01 is far too tight for that.
    ///     </para>
    ///     <para>
    ///         On a THPS1 level (k = 1, ScaleDivisor 2.25) this evaluates to
    ///         0.0556, reproducing the 0.05 already validated on School and
    ///         Downtown by <c>tools/diagnostics/n64_coplanar_probe.py</c>.
    ///     </para>
    /// </summary>
    private const float PlaneToleranceInRawUnits = 0.125f;

    /// <summary>Normal-component bucket width. Coincident faces share integer coordinates, so their normals agree to ~1e-6.</summary>
    private const float NormalTolerance = 0.02f;

    /// <summary>
    ///     Skips a plane bucket so pathological input cannot stall a conversion.
    ///     Deliberately far above anything the corpus produces — School's whole
    ///     bank is 10,781 triangles — and NOT the 64-face cap the Python probe
    ///     uses: Downtown's street lines sit on a large tessellated road plane
    ///     whose bucket exceeds 64, so that cap would drop the reported defect.
    /// </summary>
    private const int MaximumBucketSize = 20000;

    private readonly record struct PlaneKey(int X, int Y, int Z, int Distance);

    private sealed record Candidate(
        N64TriangleInstanceKey Key,
        Vector3[] Points,
        int TextureSlot,
        ushort FaceFlags,
        float Area,
        Vector3 Min,
        Vector3 Max,
        bool NormalFlipped);

    private static readonly Dictionary<N64TriangleInstanceKey, N64CoplanarOverlayAssignment> Empty = [];

    /// <summary>
    ///     Groups and ranks every flagged triangle. <paramref name="scale" /> is
    ///     one raw N64 unit expressed in export units, which is what every
    ///     tolerance here is measured in.
    /// </summary>
    internal static IReadOnlyDictionary<N64TriangleInstanceKey, N64CoplanarOverlayAssignment> FindGroups(
        IReadOnlyList<N64OverlayCandidateSource> sources,
        float scale)
    {
        var planes = CollectPlanes(sources, scale);
        if (planes.Count == 0)
            return Empty;

        var planeGroups = new List<Dictionary<N64TriangleInstanceKey, int>>();
        foreach (var candidates in planes.Values)
        {
            if (candidates.Count is < 2 or > MaximumBucketSize)
                continue;

            var overlays = new HashSet<N64TriangleInstanceKey>();
            SweepPairs(candidates, scale, overlays);
            if (overlays.Count > 0)
                planeGroups.Add(RankOverlays(candidates, overlays));
        }

        if (planeGroups.Count == 0)
            return Empty;

        var groups = new Dictionary<N64TriangleInstanceKey, N64CoplanarOverlayAssignment>();
        var groupId = 0;
        foreach (var planeGroup in planeGroups
                     .OrderBy(static group => group.Keys.Min(static key => (key.ObjectIndex, key.TriangleIndex))))
        {
            // A candidate sits in two distance buckets (see CollectPlanes), so
            // the same triangle can be flagged twice. First group wins, which
            // is deterministic because the groups are ordered by their lowest
            // key — a triangle must land in exactly one exported layer or the
            // split stops being a partition of the mesh.
            var claimed = false;
            foreach (var (key, rank) in planeGroup)
            {
                if (groups.ContainsKey(key))
                    continue;
                groups[key] = new N64CoplanarOverlayAssignment(groupId, rank);
                claimed = true;
            }

            if (claimed)
                groupId++;
        }

        return groups;
    }

    /// <summary>
    ///     Buckets candidates by plane. The distance bucket uses FLOOR and every
    ///     bucket is also paired against the next one up, because any hash
    ///     bucketing is discontinuous — two faces a thousandth apart otherwise
    ///     straddle a boundary and never meet. Normals need no such pass.
    /// </summary>
    private static Dictionary<PlaneKey, List<Candidate>> CollectPlanes(
        IReadOnlyList<N64OverlayCandidateSource> sources, float scale)
    {
        var planeTolerance = MathF.Max(PlaneToleranceInRawUnits * scale, 1e-4f);
        var planes = new Dictionary<PlaneKey, List<Candidate>>();
        foreach (var source in sources)
        {
            if (!TryCreateCandidate(source, planeTolerance, out var plane, out var candidate))
                continue;

            Add(plane, candidate);
            Add(plane with { Distance = plane.Distance + 1 }, candidate);
        }

        return planes;

        void Add(PlaneKey key, Candidate candidate)
        {
            if (!planes.TryGetValue(key, out var bucket))
            {
                bucket = [];
                planes[key] = bucket;
            }

            bucket.Add(candidate);
        }
    }

    private static bool TryCreateCandidate(
        N64OverlayCandidateSource source,
        float planeTolerance,
        out PlaneKey plane,
        out Candidate candidate)
    {
        plane = default;
        candidate = null!;
        var points = source.Points;
        if (points.Length != 3)
            return false;

        var cross = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
        var twiceArea = cross.Length();
        if (twiceArea < 1e-5f)
            return false;

        var normal = cross / twiceArea;
        var normalFlipped = CoplanarOverlayGeometry.FirstSignificantComponent(normal) < 0f;
        if (normalFlipped)
            normal = -normal;

        var distance = Vector3.Dot(normal, points[0]);
        plane = new PlaneKey(
            (int)MathF.Round(normal.X / NormalTolerance),
            (int)MathF.Round(normal.Y / NormalTolerance),
            (int)MathF.Round(normal.Z / NormalTolerance),
            (int)MathF.Floor(distance / planeTolerance));

        var min = Vector3.Min(points[0], Vector3.Min(points[1], points[2]));
        var max = Vector3.Max(points[0], Vector3.Max(points[1], points[2]));
        candidate = new Candidate(
            source.Key, points, source.TextureSlot, source.FaceFlags,
            twiceArea * 0.5f, min, max, normalFlipped);
        return true;
    }

    /// <summary>
    ///     Compares only pairs whose AABBs can still meet, by sweeping the
    ///     bucket's longer in-plane axis. A tiled floor has no overlapping
    ///     AABBs, so this collapses the naive O(n²) to roughly n·√n and the
    ///     exact clipper only ever runs on genuine overlaps.
    /// </summary>
    private static void SweepPairs(
        List<Candidate> candidates, float scale, HashSet<N64TriangleInstanceKey> overlays)
    {
        var axis = SweepAxis(candidates);
        var ordered = candidates.OrderBy(c => Component(c.Min, axis)).ToList();
        // A length, not the area fraction: a tenth of a raw unit, the same
        // "touching" allowance the PS1 detector uses against its own grid.
        var touchTolerance = 0.1f * scale;

        for (var i = 0; i < ordered.Count; i++)
        {
            var reach = Component(ordered[i].Max, axis) + touchTolerance;
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (Component(ordered[j].Min, axis) > reach)
                    break;
                ClassifyPair(ordered[i], ordered[j], overlays);
            }
        }
    }

    private static int SweepAxis(List<Candidate> candidates)
    {
        var min = candidates[0].Min;
        var max = candidates[0].Max;
        foreach (var candidate in candidates)
        {
            min = Vector3.Min(min, candidate.Min);
            max = Vector3.Max(max, candidate.Max);
        }

        var dropped = CoplanarOverlayGeometry.DominantPlaneAxis(candidates[0].Points);
        var extent = max - min;
        var first = (dropped + 1) % 3;
        var second = (dropped + 2) % 3;
        return Component(extent, first) >= Component(extent, second) ? first : second;
    }

    private static float Component(Vector3 value, int axis)
    {
        return axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
    }

    /// <summary>
    ///     Decides whether one of a coplanar pair is a decal on the other.
    ///     <para>
    ///         Only SAME-FACING pairs can fight. An opposite-facing pair is a
    ///         two-sided sheet built from two single-sided triangles, which
    ///         backface culling already resolves — the THPS1 medals are exactly
    ///         that (52 coplanar pairs, 0 same-facing), and this gate is why
    ///         characters need no separate exclusion.
    ///     </para>
    ///     <para>
    ///         The size branch is the primary rule and carries the reported
    ///         defect. It needs no draw order at all, which matters because the
    ///         PS1's ordering-table tie-break does not transfer: the N64 has a
    ///         real z-buffer. Genuinely near-equal pairs are left alone until
    ///         their tie-break is measured against the PS1 oracle.
    ///     </para>
    /// </summary>
    private static void ClassifyPair(
        Candidate first, Candidate second, HashSet<N64TriangleInstanceKey> overlays)
    {
        var doubleSided = ((first.FaceFlags | second.FaceFlags) & PsxFaceFlags.DoubleSided) != 0;
        if (first.NormalFlipped != second.NormalFlipped && !doubleSided)
            return;

        // A pair with ANY semi-transparent member is not a draw-order problem,
        // exactly as on the PS1. Measured 2026-08-07: including them made
        // Downtown flag 229 triangles where its PS1 sibling flags 25, and 199
        // of those 229 were semi-transparent — a 9x over-flag against the
        // oracle. Those faces are resolved by the writer's blanket geometric
        // lift instead (N64SemiTransparentLift), the same division of labour
        // the PS1 path uses; flagging them here as well would separate one face
        // twice.
        if (((first.FaceFlags | second.FaceFlags) & PsxFaceFlags.SemiTransparent) != 0)
            return;

        var (smaller, larger) = first.Area <= second.Area ? (first, second) : (second, first);
        if (smaller.Area >= larger.Area * DecalAreaRatio)
            return;

        if (CoplanarOverlayGeometry.HasInteriorOverlap(smaller.Points, larger.Points))
            overlays.Add(smaller.Key);
    }

    /// <summary>
    ///     Stacks mutually-overlapping flagged triangles so two decals on one
    ///     plane do not land at the same offset and keep fighting. Iterates in
    ///     display-list order — the RDP's decal mode draws only where the
    ///     surface is already in the z-buffer, so a later submission sits on
    ///     top, the inverse of the PS1's prepending ordering table.
    /// </summary>
    private static Dictionary<N64TriangleInstanceKey, int> RankOverlays(
        List<Candidate> candidates, HashSet<N64TriangleInstanceKey> overlays)
    {
        var flagged = candidates
            .Where(candidate => overlays.Contains(candidate.Key))
            .DistinctBy(static candidate => candidate.Key)
            .OrderBy(static candidate => (candidate.Key.ObjectIndex, candidate.Key.TriangleIndex))
            .ToList();

        var ranks = new Dictionary<N64TriangleInstanceKey, int>(flagged.Count);
        for (var i = 0; i < flagged.Count; i++)
        {
            var rank = 1;
            for (var j = 0; j < i; j++)
            {
                if (ranks[flagged[j].Key] >= rank
                    && CoplanarOverlayGeometry.HasInteriorOverlap(flagged[i].Points, flagged[j].Points))
                {
                    rank = ranks[flagged[j].Key] + 1;
                }
            }

            ranks[flagged[i].Key] = rank;
        }

        return ranks;
    }
}
