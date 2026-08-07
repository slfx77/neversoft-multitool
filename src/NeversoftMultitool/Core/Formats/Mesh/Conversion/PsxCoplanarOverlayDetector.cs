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
    ///     Like <see cref="Find" /> but grouped and ranked: every detected
    ///     overlay face maps to a deterministic per-plane group id plus its draw
    ///     rank WITHIN the group, so the writer can emit each (group, rank) as
    ///     its own mesh with one rigid draw-order / separation-vector metadata
    ///     record (group ids are ordered by the group's first face key). Ranks
    ///     exist because two flagged faces can overlap EACH OTHER (l8a4's
    ///     stacked baked-shadow sections): one flat rank per plane left both at
    ///     DrawIndex 1 with the same offset, still fighting (86 corpus pairs,
    ///     measured 2026-08-03). Rank follows the PS1 ordering-table paint
    ///     order — the bucket PREPENDS, so the earliest-inserted face paints
    ///     LAST (topmost) and gets the highest rank.
    /// </summary>
    internal static IReadOnlyDictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment> FindGroups(
        PsxMeshFile file)
    {
        var planes = CollectPlanes(file);

        var planeGroups = new List<Dictionary<PsxFaceInstanceKey, int>>();
        foreach (var candidates in planes.Values)
        {
            var overlays = new HashSet<PsxFaceInstanceKey>();
            for (var i = 0; i < candidates.Count; i++)
            {
                for (var j = i + 1; j < candidates.Count; j++)
                    ClassifyPair(candidates[i], candidates[j], overlays);
            }

            if (overlays.Count > 0)
                planeGroups.Add(RankOverlays(candidates, overlays));
        }

        var groups = new Dictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment>();
        var groupId = 0;
        foreach (var planeGroup in planeGroups
                     .OrderBy(static group => group.Keys.Min(static key => (key.ObjectIndex, key.FaceIndex))))
        {
            foreach (var (key, rank) in planeGroup)
                groups[key] = new PsxCoplanarOverlayAssignment(groupId, rank);
            groupId++;
        }

        return groups;
    }

    /// <summary>
    ///     Assigns draw ranks to a plane's flagged faces: iterate in PS1 paint
    ///     order (descending insertion key — the ordering table prepends, so the
    ///     last-inserted face paints first) and stack each face one rank above
    ///     the highest-ranked already-painted face it actually overlaps (exact
    ///     clipped shared area, same rule as the flag itself). Faces that
    ///     overlap no other flagged face keep rank 1 — the pre-rank behaviour.
    /// </summary>
    private static Dictionary<PsxFaceInstanceKey, int> RankOverlays(
        List<Candidate> candidates,
        HashSet<PsxFaceInstanceKey> overlays)
    {
        var flagged = candidates
            .Where(candidate => overlays.Contains(candidate.Key))
            .OrderByDescending(static candidate => (candidate.Key.ObjectIndex, candidate.Key.FaceIndex))
            .ToList();

        var ranks = new Dictionary<PsxFaceInstanceKey, int>(flagged.Count);
        for (var i = 0; i < flagged.Count; i++)
        {
            var rank = 1;
            for (var j = 0; j < i; j++)
            {
                if (ranks[flagged[j].Key] >= rank
                    && HasInteriorOverlap(flagged[i], flagged[j]))
                {
                    rank = ranks[flagged[j].Key] + 1;
                }
            }

            ranks[flagged[i].Key] = rank;
        }

        return ranks;
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

    /// <summary>
    ///     An exact twin draws the IDENTICAL fragments, so the pair is stable
    ///     without separation. That requires identical geometry AND identical
    ///     appearance — face colour, mode, and shading kind, plus the UVs.
    ///     Bounds/area equality alone skipped the corpus' single biggest
    ///     unseparated class (872 pairs, 2026-08-03): baked light/shadow
    ///     duplicates and tinted window/wall variants are exactly-coincident
    ///     same-texture faces whose COLOURS differ (skware o117f9/o151f9:
    ///     (108,189,156) day vs (34,73,52) shadowed, frac=1.00), which dither
    ///     as densely as any decal. Raw <c>Flags</c> is deliberately NOT
    ///     compared — bits that never change the drawn fragment (e.g. 0x4000)
    ///     differ on harmless true twins.
    /// </summary>
    private static bool IsExactTwin(Candidate first, Candidate second)
    {
        const float epsilon = 0.01f;
        return HasIdenticalAppearance(first.Face, second.Face)
               && Vector3.Distance(first.Min, second.Min) < epsilon
               && Vector3.Distance(first.Max, second.Max) < epsilon
               && MathF.Abs(first.Area - second.Area) < first.Area * 0.001f + epsilon;
    }

    private static bool HasIdenticalAppearance(PsxFace first, PsxFace second)
    {
        return first.R == second.R
               && first.G == second.G
               && first.B == second.B
               && first.Mode == second.Mode
               && first.IsGouraud == second.IsGouraud
               && first.U0 == second.U0 && first.V0 == second.V0
               && first.U1 == second.U1 && first.V1 == second.V1
               && first.U2 == second.U2 && first.V2 == second.V2
               && first.U3 == second.U3 && first.V3 == second.V3;
    }

    /// <summary>
    ///     PS1 AABB-penetration tolerances, in world units. The authoring grid
    ///     step is 1/2.25 ≈ 0.44, so a tenth of it is "touching" and a quarter of
    ///     it is real interpenetration. They live here rather than in
    ///     <see cref="CoplanarOverlayGeometry" /> because they are calibrated
    ///     against THIS format's grid — an N64 build stores
    ///     <c>trunc(PS1raw / k)</c> and needs its own.
    /// </summary>
    private const float PsxTouchTolerance = 0.1f;

    private const float PsxRealOverlap = 0.25f;

    private static bool BoundsOverlap(
        (Vector3 Min, Vector3 Max) first,
        (Vector3 Min, Vector3 Max) second)
    {
        return CoplanarOverlayGeometry.BoundsOverlap(
            first, second, PsxTouchTolerance, PsxRealOverlap);
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
        var normalFlipped = CoplanarOverlayGeometry.FirstSignificantComponent(normal) < 0f;
        if (normalFlipped)
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

        candidate = new Candidate(key, face, points, area, centroid, min, max, normalFlipped);
        return true;
    }

    private static void ClassifyPair(
        Candidate first,
        Candidate second,
        HashSet<PsxFaceInstanceKey> overlays)
    {
        // Back-to-back single-sided faces (a wall authored once per side) land
        // in one bucket because the plane key canonicalizes the normal sign,
        // but they never rasterize together — backface culling shows at most
        // one per viewpoint — so they cannot fight and must not be flagged.
        // Verifier-measured 2026-08-03: without this the appearance-narrowed
        // twin rule below roughly doubled the corpus flags (+5.3k) on pairs
        // culling already separates. Double-sided faces stay in: both render.
        if (first.NormalFlipped != second.NormalFlipped
            && !first.Face.IsDoubleSided
            && !second.Face.IsDoubleSided)
        {
            return;
        }

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
            // Overlap is the same EXACT clipped-shared-area rule the
            // near-equal branch uses (one semantic in both branches). The
            // former centroid-inside gate silently dropped every partial
            // overlap where neither centroid lands inside its partner — 335
            // corpus pairs (2026-08-03), 100% of SKB1/SKB2's unseparated
            // class, with real misses up to 0.44 of the smaller face.
            if (HasInteriorOverlap(smaller, larger))
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

    /// <summary>
    ///     True when two coplanar faces share enough area to be a real overlay.
    ///     The geometry lives in <see cref="CoplanarOverlayGeometry" /> so the
    ///     N64 detector uses the identical rule and threshold.
    /// </summary>
    private static bool HasInteriorOverlap(Candidate first, Candidate second)
    {
        return CoplanarOverlayGeometry.HasInteriorOverlap(first.Points, second.Points);
    }

    private readonly record struct PlaneKey(int X, int Y, int Z, int Distance);

    /// <summary>
    ///     <paramref name="NormalFlipped" /> records whether the face's raw
    ///     winding normal was negated to reach the canonical plane key — two
    ///     candidates in one bucket face OPPOSITE directions iff their flags
    ///     differ (the back-to-back wall case).
    /// </summary>
    private sealed record Candidate(
        PsxFaceInstanceKey Key,
        PsxFace Face,
        Vector3[] Points,
        float Area,
        Vector3 Centroid,
        Vector3 Min,
        Vector3 Max,
        bool NormalFlipped);
}

/// <summary>
///     A detected overlay face's per-plane group id plus its draw rank within
///     the group (1 = the first-painted overlay layer; a face stacks one rank
///     above the highest-ranked flagged face it overlaps). The writer emits one
///     mesh per (group, rank) so mutually overlapping overlays get distinct
///     DrawIndex values and stacked separation offsets.
/// </summary>
internal readonly record struct PsxCoplanarOverlayAssignment(int GroupId, int DrawRank);
