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
        var discovery = DiscoverComparisons(file);

        var planeGroups = new List<Dictionary<PsxFaceInstanceKey, int>>();
        foreach (var component in BuildComparisonComponents(discovery))
        {
            var overlays = new HashSet<PsxFaceInstanceKey>();
            foreach (var pair in component.Pairs)
            {
                ClassifyPair(
                    pair.First,
                    pair.Second,
                    pair.FirstPlane,
                    pair.SecondPlane,
                    overlays,
                    out _);
            }

            if (overlays.Count > 0)
            {
                planeGroups.Add(RankOverlays(
                    component.Candidates,
                    overlays,
                    component.ComparablePairs));
            }
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
    ///     Reports the result of every same-plane face-pair comparison made by
    ///     the shipped detector. Diagnostics deliberately call
    ///     <see cref="ClassifyPair" /> rather than duplicating its rules, so a
    ///     residue investigation observes the exact branch that production
    ///     used. A successful comparison names the selected overlay; a
    ///     declined comparison names the reason and has no overlay.
    /// </summary>
    internal static IEnumerable<PsxCoplanarPairDiagnostic> DiagnosePairs(PsxMeshFile file)
    {
        var discovery = DiscoverComparisons(file);
        foreach (var pair in discovery.Pairs.Values
                     .OrderBy(static pair => (pair.First.Key.ObjectIndex, pair.First.Key.FaceIndex))
                     .ThenBy(static pair => (pair.Second.Key.ObjectIndex, pair.Second.Key.FaceIndex)))
        {
            // ClassifyPair only adds to this set; prior additions do not affect
            // later decisions. Keeping the real accumulator here makes this
            // the same call shape as FindGroups, not a parallel classifier.
            var overlays = new HashSet<PsxFaceInstanceKey>();
            var overlay = ClassifyPair(
                pair.First,
                pair.Second,
                pair.FirstPlane,
                pair.SecondPlane,
                overlays,
                out var declineReason);
            yield return new PsxCoplanarPairDiagnostic(
                pair.First.Key,
                pair.Second.Key,
                overlay,
                declineReason,
                null,
                DescribePlanes(pair.First),
                DescribePlanes(pair.Second))
            {
                AdmittedPlaneDistanceDelta = MathF.Abs(
                    pair.FirstPlane.RawDistance - pair.SecondPlane.RawDistance),
                FirstAdmissionUsesPrimaryTriangle = pair.FirstPlane.IsPrimary,
                SecondAdmissionUsesPrimaryTriangle = pair.SecondPlane.IsPrimary,
                SharedAreaFraction = CoplanarOverlayGeometry.CoplanarSharedAreaFraction(
                    pair.First.Points,
                    pair.Second.Points),
                AdmittedTriangleSharedAreaFraction = AdmittedTriangleSharedAreaFraction(pair),
                FirstArea = pair.First.Area,
                SecondArea = pair.Second.Area
            };
        }
    }

    /// <summary>
    ///     Diagnoses a requested source-face pair even when production never
    ///     compared it. This separates a <see cref="ClassifyPair" /> decline
    ///     from candidate rejection and plane-bucket mismatch — an important
    ///     distinction for exported triangle pairs originating in warped
    ///     quads, because production assigns one plane to the whole face.
    /// </summary>
    internal static PsxCoplanarPairDiagnostic DiagnosePair(
        PsxMeshFile file,
        PsxFaceInstanceKey firstKey,
        PsxFaceInstanceKey secondKey)
    {
        if (firstKey == secondKey)
        {
            return new PsxCoplanarPairDiagnostic(
                firstKey,
                secondKey,
                null,
                null,
                PsxCoplanarPairNotComparedReason.SameFace,
                null,
                null);
        }

        var discovery = DiscoverComparisons(file);
        var hasFirst = TryFindCandidate(discovery.Candidates, firstKey, out var first);
        var hasSecond = TryFindCandidate(discovery.Candidates, secondKey, out var second);
        PsxCoplanarFacePlaneKeys? firstPlanes = hasFirst ? DescribePlanes(first) : null;
        PsxCoplanarFacePlaneKeys? secondPlanes = hasSecond ? DescribePlanes(second) : null;
        if (!hasFirst || !hasSecond)
        {
            var reason = (hasFirst, hasSecond) switch
            {
                (false, false) => PsxCoplanarPairNotComparedReason.BothFacesHaveNoCandidate,
                (false, true) => PsxCoplanarPairNotComparedReason.FirstFaceHasNoCandidate,
                _ => PsxCoplanarPairNotComparedReason.SecondFaceHasNoCandidate
            };
            return new PsxCoplanarPairDiagnostic(
                firstKey,
                secondKey,
                null,
                null,
                reason,
                firstPlanes,
                secondPlanes);
        }

        var pairKey = CandidatePairKey.Create(firstKey, secondKey);
        if (!discovery.Pairs.TryGetValue(pairKey, out var pair))
        {
            return new PsxCoplanarPairDiagnostic(
                firstKey,
                secondKey,
                null,
                null,
                PsxCoplanarPairNotComparedReason.DifferentPlaneBuckets,
                firstPlanes,
                secondPlanes);
        }

        var requestedInDiscoveryOrder = pair.First.Key == firstKey;
        var secondPlane = requestedInDiscoveryOrder
            ? pair.SecondPlane
            : pair.FirstPlane;
        var firstPlane = requestedInDiscoveryOrder
            ? pair.FirstPlane
            : pair.SecondPlane;
        var overlay = ClassifyPair(
            first,
            second,
            firstPlane,
            secondPlane,
            [],
            out var declineReason);
        return new PsxCoplanarPairDiagnostic(
            firstKey,
            secondKey,
            overlay,
            declineReason,
            null,
            firstPlanes,
            secondPlanes)
        {
            AdmittedPlaneDistanceDelta = MathF.Abs(
                pair.FirstPlane.RawDistance - pair.SecondPlane.RawDistance),
            FirstAdmissionUsesPrimaryTriangle = requestedInDiscoveryOrder
                ? pair.FirstPlane.IsPrimary
                : pair.SecondPlane.IsPrimary,
            SecondAdmissionUsesPrimaryTriangle = requestedInDiscoveryOrder
                ? pair.SecondPlane.IsPrimary
                : pair.FirstPlane.IsPrimary,
            SharedAreaFraction = CoplanarOverlayGeometry.CoplanarSharedAreaFraction(
                first.Points,
                second.Points),
            AdmittedTriangleSharedAreaFraction = AdmittedTriangleSharedAreaFraction(pair),
            FirstArea = first.Area,
            SecondArea = second.Area
        };
    }

    private static bool TryFindCandidate(
        IReadOnlyList<Candidate> candidates,
        PsxFaceInstanceKey key,
        out Candidate candidate)
    {
        foreach (var item in candidates)
        {
            if (item.Key != key)
                continue;

            candidate = item;
            return true;
        }

        candidate = null!;
        return false;
    }

    private static PsxCoplanarFacePlaneKeys DescribePlanes(Candidate candidate)
    {
        var secondary = candidate.SecondaryPlane.HasValue
            ? ToDiagnosticPlaneKey(candidate.SecondaryPlane.Value.Key)
            : (PsxCoplanarPlaneKey?)null;

        return new PsxCoplanarFacePlaneKeys(
            ToDiagnosticPlaneKey(candidate.PrimaryPlane.Key),
            secondary);
    }

    private static PsxCoplanarPlaneKey ToDiagnosticPlaneKey(PlaneKey plane)
    {
        return new PsxCoplanarPlaneKey(plane.X, plane.Y, plane.Z, plane.Distance);
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
        HashSet<PsxFaceInstanceKey> overlays,
        IReadOnlyDictionary<CandidatePairKey, CandidateComparison> comparablePairs)
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
                var pairKey = CandidatePairKey.Create(flagged[i].Key, flagged[j].Key);
                if (ranks[flagged[j].Key] >= rank
                    && comparablePairs.TryGetValue(pairKey, out var comparison)
                    && HasInteriorOverlap(comparison))
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
        return FindSemiTransparentLayerSteps(file, FindGroups(file));
    }

    /// <summary>
    ///     Overload taking the file's already-computed opaque overlay
    ///     assignments so the writer (which needs both) computes
    ///     <see cref="FindGroups" /> once.
    /// </summary>
    internal static IReadOnlyDictionary<PsxFaceInstanceKey, int> FindSemiTransparentLayerSteps(
        PsxMeshFile file,
        IReadOnlyDictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment> opaqueOverlays)
    {
        Dictionary<PsxFaceInstanceKey, int>? steps = null;
        foreach (var candidates in CollectPlanes(file).Values)
        {
            AccumulateTransparentLayerPairSteps(candidates, ref steps);
            AccumulateOpaqueOverlayClearanceSteps(candidates, opaqueOverlays, ref steps);
        }

        return steps ?? (IReadOnlyDictionary<PsxFaceInstanceKey, int>)EmptySteps;
    }

    /// <summary>
    ///     Stacked semi-transparent layers of DIFFERENT textures on one plane:
    ///     the top layer of each pair takes one extra step (SKB2's animated
    ///     waves over its static water). Overlap is decided per face pair,
    ///     never from group union bounds — union bounds chain side-by-side
    ///     sign panels into deep stacks.
    /// </summary>
    private static void AccumulateTransparentLayerPairSteps(
        List<Candidate> candidates,
        ref Dictionary<PsxFaceInstanceKey, int>? steps)
    {
        var layerGroups = candidates
            .Where(static candidate => candidate.Face.IsSemiTransparent)
            .GroupBy(static candidate => (candidate.Face.TextureHash, candidate.Face.IsTextured))
            .Select(static group => group.ToList())
            .ToList();
        if (layerGroups.Count < 2)
            return;

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
                    steps[candidate.Key] = Math.Max(steps.GetValueOrDefault(candidate.Key, 1), 2);
                }
            }
        }
    }

    /// <summary>
    ///     A semi-transparent face must also clear any OPAQUE draw-order
    ///     overlay it overlaps on its own plane. Both anti-z-fighting
    ///     mechanisms apply one 0.25 step from the same authored plane — the
    ///     opaque overlay through its node-transform BlendOffset, the
    ///     semi-transparent face through its baked vertex lift — so without
    ///     this rule they land exactly coplanar and recreate the fight both
    ///     were built to avoid (THPS2 PSX skb1: 12 duplicate pool-floor
    ///     overlays and 32 water sheets all met at one lifted plane, 46
    ///     overlapping pairs). The transparent surface is the top of any such
    ///     stack — the engine draws it over the floor it covers — so it steps
    ///     to the overlapped overlay's rank + 1.
    /// </summary>
    /// <remarks>
    ///     The two classifiers themselves stay deliberately blind to each
    ///     other: <c>ClassifyPair</c> declines pairs with a semi-transparent
    ///     member (the face lifts geometrically instead of being flagged), and
    ///     the layer rule above compares transparent textures only. This rule
    ///     is the one place the two mechanisms are reconciled, and it only
    ///     ever RAISES a step count — membership of both sets is untouched.
    ///     Steps cap at <see cref="MaxSemiTransparentLiftSteps" /> = 3,
    ///     measured: the deepest overlapped opaque rank in the twelve-level
    ///     corpus is 2 (skware's pool, whose water sheet crosses a rank-2
    ///     overlay stack and so needs step 3), every other level needs at
    ///     most rank 1 + 1 = 2. The cap must exceed the deepest overlapped
    ///     rank — clamping to the rank itself would park the sheet AT the
    ///     overlay's height and preserve the collision — so anything past 3
    ///     is evidence of a mis-grouped plane, not authored layering.
    /// </remarks>
    private static void AccumulateOpaqueOverlayClearanceSteps(
        List<Candidate> candidates,
        IReadOnlyDictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment> opaqueOverlays,
        ref Dictionary<PsxFaceInstanceKey, int>? steps)
    {
        if (opaqueOverlays.Count == 0)
            return;

        List<(Candidate Candidate, int Rank)>? flaggedOpaque = null;
        foreach (var candidate in candidates)
        {
            if (!candidate.Face.IsSemiTransparent &&
                opaqueOverlays.TryGetValue(candidate.Key, out var assignment))
            {
                flaggedOpaque ??= [];
                flaggedOpaque.Add((candidate, assignment.DrawRank));
            }
        }

        if (flaggedOpaque == null)
            return;

        foreach (var candidate in candidates)
        {
            if (!candidate.Face.IsSemiTransparent)
                continue;

            var maxRank = 0;
            foreach (var (opaque, rank) in flaggedOpaque)
            {
                if (rank > maxRank &&
                    BoundsOverlap((candidate.Min, candidate.Max), (opaque.Min, opaque.Max)))
                {
                    maxRank = rank;
                }
            }

            if (maxRank == 0)
                continue;

            var required = Math.Min(maxRank + 1, MaxSemiTransparentLiftSteps);
            steps ??= [];
            steps[candidate.Key] = Math.Max(steps.GetValueOrDefault(candidate.Key, 1), required);
        }
    }

    /// <summary>
    ///     Upper bound on the semi-transparent lift's step count. See
    ///     <see cref="AccumulateOpaqueOverlayClearanceSteps" />.
    /// </summary>
    private const int MaxSemiTransparentLiftSteps = 3;

    /// <summary>
    ///     Measurement used by the corpus tests: the highest opaque-overlay
    ///     draw rank any semi-transparent face actually overlaps. The cap in
    ///     <see cref="AccumulateOpaqueOverlayClearanceSteps" /> is only exact
    ///     while this stays at 1 corpus-wide — a face over a rank-2 overlay
    ///     would need step 3, and clamping it to 2 would recreate the
    ///     collision at the overlay's own height.
    /// </summary>
    internal static int MeasureMaxOpaqueRankUnderSemiTransparent(
        PsxMeshFile file,
        IReadOnlyDictionary<PsxFaceInstanceKey, PsxCoplanarOverlayAssignment> opaqueOverlays)
    {
        var maxRank = 0;
        if (opaqueOverlays.Count == 0)
            return maxRank;

        foreach (var candidates in CollectPlanes(file).Values)
        {
            var flaggedOpaque = candidates
                .Where(candidate => !candidate.Face.IsSemiTransparent &&
                                    opaqueOverlays.ContainsKey(candidate.Key))
                .ToList();
            if (flaggedOpaque.Count == 0)
                continue;

            foreach (var candidate in candidates)
            {
                if (!candidate.Face.IsSemiTransparent)
                    continue;

                foreach (var opaque in flaggedOpaque)
                {
                    var rank = opaqueOverlays[opaque.Key].DrawRank;
                    if (rank > maxRank &&
                        BoundsOverlap((candidate.Min, candidate.Max), (opaque.Min, opaque.Max)))
                    {
                        maxRank = rank;
                    }
                }
            }
        }

        return maxRank;
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

    /// <summary>
    ///     The semi-transparent layer detector deliberately keeps the original
    ///     primary-triangle buckets. Its lift rule was calibrated separately
    ///     from opaque draw-order overlays; secondary triangles and adjacent
    ///     distance buckets are evidence for the latter only.
    /// </summary>
    private static Dictionary<PlaneKey, List<Candidate>> CollectPlanes(PsxMeshFile file)
    {
        var planes = new Dictionary<PlaneKey, List<Candidate>>();
        foreach (var candidate in CollectCandidates(file, resolveSpriteVertices: false))
        {
            var plane = candidate.PrimaryPlane.Key;
            if (!planes.TryGetValue(plane, out var candidates))
            {
                candidates = [];
                planes.Add(plane, candidates);
            }

            candidates.Add(candidate);
        }

        return planes;
    }

    private static List<Candidate> CollectCandidates(
        PsxMeshFile file,
        bool resolveSpriteVertices)
    {
        var candidates = new List<Candidate>();
        for (var objectIndex = 0; objectIndex < file.Objects.Count; objectIndex++)
        {
            var obj = file.Objects[objectIndex];
            if (obj.MeshIndex >= file.Meshes.Count)
                continue;

            var mesh = file.Meshes[obj.MeshIndex];
            var spriteResolver = resolveSpriteVertices
                ? PsxSpriteVertexResolver.TryCreate(mesh)
                : null;
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(file, obj));
            for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
            {
                var face = mesh.Faces[faceIndex];
                if (!TryCreateCandidate(
                        candidates.Count,
                        new PsxFaceInstanceKey(objectIndex, faceIndex),
                        mesh,
                        face,
                        offset,
                        spriteResolver,
                        out var candidate))
                    continue;

                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>
    ///     Builds the opaque detector's exact comparison set. A face may enter
    ///     through either rendered triangle of a quad, and two triangle planes
    ///     may differ by one DISTANCE bucket after 1/100 quantisation. Normal
    ///     components must still match exactly: accepting their neighbours as
    ///     well would turn this narrow rounding seam into an angular tolerance.
    ///     Each source-face pair is retained once, using its closest and most
    ///     primary admission, so diagnostics and grouping cannot duplicate it.
    /// </summary>
    private static ComparisonDiscovery DiscoverComparisons(PsxMeshFile file)
    {
        // Opaque comparisons must observe the same points the writer emits.
        // Type-bit sprite vertices store anchor/mate offsets rather than
        // positions, so their head-on billboard corners are resolved here.
        // The separately calibrated semi-transparent lift detector retains
        // its legacy raw candidate path through CollectPlanes.
        var candidates = CollectCandidates(file, resolveSpriteVertices: true);
        var indexedPlanes = new Dictionary<PlaneKey, List<CandidatePlaneInstance>>();
        var pairs = new Dictionary<CandidatePairKey, CandidateComparison>();

        foreach (var candidate in candidates)
        {
            foreach (var candidatePlane in EnumerateDistinctPlanes(candidate))
            {
                for (var distanceDelta = -1; distanceDelta <= 1; distanceDelta++)
                {
                    var lookup = candidatePlane.Key with
                    {
                        Distance = candidatePlane.Key.Distance + distanceDelta
                    };
                    if (!indexedPlanes.TryGetValue(lookup, out var priorPlanes))
                        continue;

                    foreach (var prior in priorPlanes)
                    {
                        if (prior.Candidate.DiscoveryIndex == candidate.DiscoveryIndex)
                            continue;

                        var legacyPrimaryBucket = prior.Plane.IsPrimary
                                                  && candidatePlane.IsPrimary
                                                  && prior.Plane.Key == candidatePlane.Key;
                        if (!legacyPrimaryBucket
                            && MathF.Abs(prior.Plane.RawDistance - candidatePlane.RawDistance)
                            > PsxAdjacentPlaneDistanceTolerance)
                        {
                            continue;
                        }

                        var comparison = new CandidateComparison(
                            prior.Candidate,
                            candidate,
                            prior.Plane,
                            candidatePlane);
                        var key = CandidatePairKey.Create(prior.Candidate.Key, candidate.Key);
                        if (!pairs.TryGetValue(key, out var existing)
                            || IsBetterAdmission(comparison, existing))
                        {
                            pairs[key] = comparison;
                        }
                    }
                }

                if (!indexedPlanes.TryGetValue(candidatePlane.Key, out var samePlane))
                {
                    samePlane = [];
                    indexedPlanes.Add(candidatePlane.Key, samePlane);
                }

                samePlane.Add(new CandidatePlaneInstance(candidate, candidatePlane));
            }
        }

        return new ComparisonDiscovery(candidates, pairs);
    }

    private static IEnumerable<CandidatePlane> EnumerateDistinctPlanes(Candidate candidate)
    {
        yield return candidate.PrimaryPlane;
        if (candidate.SecondaryPlane is { } secondary
            && secondary != candidate.PrimaryPlane)
        {
            yield return secondary;
        }
    }

    private static bool IsBetterAdmission(
        CandidateComparison candidate,
        CandidateComparison existing)
    {
        return AdmissionPriority(candidate).CompareTo(AdmissionPriority(existing)) < 0;

        static (int DistanceDelta, int SecondaryCount, int FirstDistance, int SecondDistance,
            int FirstFlipped, int SecondFlipped) AdmissionPriority(CandidateComparison comparison)
        {
            return (
                Math.Abs(comparison.FirstPlane.Key.Distance - comparison.SecondPlane.Key.Distance),
                (comparison.FirstPlane.IsPrimary ? 0 : 1)
                + (comparison.SecondPlane.IsPrimary ? 0 : 1),
                comparison.FirstPlane.Key.Distance,
                comparison.SecondPlane.Key.Distance,
                comparison.FirstPlane.NormalFlipped ? 1 : 0,
                comparison.SecondPlane.NormalFlipped ? 1 : 0);
        }
    }

    private static float AdmittedTriangleSharedAreaFraction(CandidateComparison comparison)
    {
        return CoplanarOverlayGeometry.CoplanarSharedAreaFraction(
            GetAdmittedTriangle(comparison.First, comparison.FirstPlane),
            GetAdmittedTriangle(comparison.Second, comparison.SecondPlane));
    }

    private static Vector3[] GetAdmittedTriangle(Candidate candidate, CandidatePlane plane)
    {
        return plane.IsPrimary
            ? [candidate.Points[0], candidate.Points[2], candidate.Points[1]]
            : [candidate.Points[1], candidate.Points[2], candidate.Points[3]];
    }

    /// <summary>
    ///     A quad can bridge its two triangle planes and adjacent distance
    ///     buckets can bridge quantisation seams. Union those comparison edges
    ///     once so every face receives at most one deterministic group/rank.
    ///     Ranking still tests the direct comparison set, preventing a chain at
    ///     distances d/d+1/d+2 from treating the endpoints as coplanar.
    /// </summary>
    private static IEnumerable<ComparisonComponent> BuildComparisonComponents(
        ComparisonDiscovery discovery)
    {
        if (discovery.Pairs.Count == 0)
            yield break;

        var parents = Enumerable.Range(0, discovery.Candidates.Count).ToArray();
        var orderedPairs = discovery.Pairs.Values
            .OrderBy(static pair => pair.First.DiscoveryIndex)
            .ThenBy(static pair => pair.Second.DiscoveryIndex)
            .ToList();
        foreach (var pair in orderedPairs)
            Union(pair.First.DiscoveryIndex, pair.Second.DiscoveryIndex);

        var builders = new Dictionary<int, ComponentBuilder>();
        foreach (var pair in orderedPairs)
        {
            var root = FindRoot(pair.First.DiscoveryIndex);
            if (!builders.TryGetValue(root, out var builder))
            {
                builder = new ComponentBuilder();
                builders.Add(root, builder);
            }

            builder.CandidateIndices.Add(pair.First.DiscoveryIndex);
            builder.CandidateIndices.Add(pair.Second.DiscoveryIndex);
            builder.Pairs.Add(pair);
        }

        foreach (var builder in builders.Values
                     .OrderBy(static builder => builder.CandidateIndices.Min()))
        {
            var componentCandidates = builder.CandidateIndices
                .Order()
                .Select(index => discovery.Candidates[index])
                .ToList();
            var comparablePairs = builder.Pairs.ToDictionary(
                static pair => CandidatePairKey.Create(pair.First.Key, pair.Second.Key));
            yield return new ComparisonComponent(
                componentCandidates,
                builder.Pairs,
                comparablePairs);
        }

        int FindRoot(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int first, int second)
        {
            var firstRoot = FindRoot(first);
            var secondRoot = FindRoot(second);
            if (firstRoot == secondRoot)
                return;

            // Always attach the higher discovery index. Component identity is
            // therefore independent of dictionary insertion/runtime hashing.
            if (firstRoot < secondRoot)
                parents[secondRoot] = firstRoot;
            else
                parents[firstRoot] = secondRoot;
        }
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

    /// <summary>
    ///     Raw plane-distance seam admitted in addition to the legacy exact
    ///     primary bucket. Quantised neighbouring buckets alone span almost
    ///     0.02 world units at their far edges; the six independent round-3
    ///     residue pairs instead measure 0..0.0048828125. A 0.005 cap covers
    ///     that evidence while staying below half of one 1/100 distance bucket
    ///     and about 1/89 of the PS1 authoring-grid step.
    /// </summary>
    private const float PsxAdjacentPlaneDistanceTolerance = 0.005f;

    private static bool BoundsOverlap(
        (Vector3 Min, Vector3 Max) first,
        (Vector3 Min, Vector3 Max) second)
    {
        return CoplanarOverlayGeometry.BoundsOverlap(
            first, second, PsxTouchTolerance, PsxRealOverlap);
    }

    private static bool TryCreateCandidate(
        int discoveryIndex,
        PsxFaceInstanceKey key,
        PsxMesh mesh,
        PsxFace face,
        Vector3 offset,
        PsxSpriteVertexResolver? spriteResolver,
        out Candidate candidate)
    {
        candidate = null!;
        var count = face.IsQuad ? 4 : 3;
        var points = new Vector3[count];
        for (var slot = 0; slot < count; slot++)
        {
            var vertexIndex = PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot);
            if (vertexIndex >= mesh.Vertices.Count)
                return false;
            var vertex = mesh.Vertices[(int)vertexIndex];
            points[slot] = (spriteResolver != null
                            && spriteResolver.TryResolvePosition(vertexIndex, out var spriteCorner)
                ? spriteCorner
                : new Vector3(vertex.X, -vertex.Y, -vertex.Z)) + offset;
        }

        if (!TryCreatePlaneKey(
                points[0],
                points[2],
                points[1],
                out var primaryPlane,
                out var twiceFirstArea,
                out var normalFlipped,
                out var primaryDistance))
        {
            return false;
        }

        var primary = new CandidatePlane(primaryPlane, normalFlipped, true, primaryDistance);
        CandidatePlane? secondary = null;
        if (face.IsQuad
            && TryCreatePlaneKey(
                points[1],
                points[2],
                points[3],
                out var secondaryPlane,
                out _,
                out var secondaryNormalFlipped,
                out var secondaryDistance))
        {
            secondary = new CandidatePlane(
                secondaryPlane,
                secondaryNormalFlipped,
                false,
                secondaryDistance);
        }

        var area = twiceFirstArea * 0.5f;
        if (face.IsQuad)
            area += Vector3.Cross(points[2] - points[1], points[3] - points[1]).Length() * 0.5f;
        var centroid = points.Aggregate(Vector3.Zero, static (sum, point) => sum + point) / points.Length;
        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        candidate = new Candidate(
            discoveryIndex,
            key,
            face,
            points,
            area,
            centroid,
            min,
            max,
            primary,
            secondary);
        return true;
    }

    /// <summary>
    ///     Builds the exact quantised key production uses for one candidate
    ///     triangle. Callers pass source-face positions in emitted winding
    ///     order: PSX face half 0 is (0,2,1), and quad half 1 is (1,2,3).
    /// </summary>
    private static bool TryCreatePlaneKey(
        Vector3 first,
        Vector3 second,
        Vector3 third,
        out PlaneKey plane,
        out float twiceArea,
        out bool normalFlipped,
        out float rawDistance)
    {
        plane = default;
        normalFlipped = false;
        rawDistance = 0f;
        var cross = Vector3.Cross(second - first, third - first);
        twiceArea = cross.Length();
        if (twiceArea < 1e-5f)
            return false;

        var normal = cross / twiceArea;
        normalFlipped = CoplanarOverlayGeometry.FirstSignificantComponent(normal) < 0f;
        if (normalFlipped)
            normal = -normal;
        rawDistance = Vector3.Dot(normal, first);
        plane = new PlaneKey(
            (int)MathF.Round(normal.X * 1000f),
            (int)MathF.Round(normal.Y * 1000f),
            (int)MathF.Round(normal.Z * 1000f),
            (int)MathF.Round(rawDistance * 100f));
        return true;
    }

    private static PsxFaceInstanceKey? ClassifyPair(
        Candidate first,
        Candidate second,
        CandidatePlane firstPlane,
        CandidatePlane secondPlane,
        HashSet<PsxFaceInstanceKey> overlays,
        out PsxCoplanarPairDeclineReason? declineReason)
    {
        declineReason = null;

        // Back-to-back single-sided faces (a wall authored once per side) land
        // in one bucket because the plane key canonicalizes the normal sign,
        // but they never rasterize together — backface culling shows at most
        // one per viewpoint — so they cannot fight and must not be flagged.
        // Verifier-measured 2026-08-03: without this the appearance-narrowed
        // twin rule below roughly doubled the corpus flags (+5.3k) on pairs
        // culling already separates. Double-sided faces stay in: both render.
        if (firstPlane.NormalFlipped != secondPlane.NormalFlipped
            && !first.Face.IsDoubleSided
            && !second.Face.IsDoubleSided)
        {
            declineReason = PsxCoplanarPairDeclineReason.BackToBackSingleSided;
            return null;
        }

        if (first.Face.TextureHash == second.Face.TextureHash
            && first.Face.IsTextured == second.Face.IsTextured)
        {
            if (IsExactTwin(first, second))
            {
                // Same-texture exact whole-face twins draw identical fragments
                // and are stable without separation.
                declineReason = PsxCoplanarPairDeclineReason.SameTextureExactTwin;
                return null;
            }

            if (!BoundsOverlap((first.Min, first.Max), (second.Min, second.Max)))
            {
                // Edge-adjacent/disjoint tiling stays untouched. Same-texture
                // DIFFERENT-shape overlaps (l2a1's start rooftop patches its
                // gravel with offset quads of the same texture) fall through.
                declineReason = PsxCoplanarPairDeclineReason.SameTextureWithoutBoundsOverlap;
                return null;
            }
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
        {
            declineReason = PsxCoplanarPairDeclineReason.SemiTransparentMember;
            return null;
        }

        if (smaller.Area < larger.Area * 0.95f)
        {
            // The classic decal: a clearly smaller face authored on a base.
            // Overlap is the same EXACT clipped-shared-area rule the
            // near-equal branch uses (one semantic in both branches). The
            // former centroid-inside gate silently dropped every partial
            // overlap where neither centroid lands inside its partner — 335
            // corpus pairs (2026-08-03), 100% of SKB1/SKB2's unseparated
            // class, with real misses up to 0.44 of the smaller face.
            var smallerPlane = ReferenceEquals(smaller, first) ? firstPlane : secondPlane;
            var largerPlane = ReferenceEquals(smaller, first) ? secondPlane : firstPlane;
            if (!HasInteriorOverlap(smaller, smallerPlane, larger, largerPlane))
            {
                declineReason = PsxCoplanarPairDeclineReason.SmallerFaceHasInsufficientSharedArea;
                return null;
            }

            overlays.Add(smaller.Key);
            return smaller.Key;
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
        if (!BoundsOverlap((first.Min, first.Max), (second.Min, second.Max)))
        {
            declineReason = PsxCoplanarPairDeclineReason.NearEqualWithoutBoundsOverlap;
            return null;
        }

        if (!HasInteriorOverlap(first, firstPlane, second, secondPlane))
        {
            declineReason = PsxCoplanarPairDeclineReason.NearEqualHasInsufficientSharedArea;
            return null;
        }

        var firstKey = (first.Key.ObjectIndex, first.Key.FaceIndex);
        var secondKey = (second.Key.ObjectIndex, second.Key.FaceIndex);
        var overlay = firstKey.CompareTo(secondKey) <= 0 ? first.Key : second.Key;
        overlays.Add(overlay);
        return overlay;
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

    private static bool HasInteriorOverlap(CandidateComparison comparison)
    {
        return HasInteriorOverlap(
            comparison.First,
            comparison.FirstPlane,
            comparison.Second,
            comparison.SecondPlane);
    }

    private static bool HasInteriorOverlap(
        Candidate first,
        CandidatePlane firstPlane,
        Candidate second,
        CandidatePlane secondPlane)
    {
        // Primary-primary comparisons keep the historical whole-face
        // classifier. When a secondary triangle admits a warped quad, only the
        // two triangles established as coplanar may prove interior overlap;
        // projecting the WHOLE quad promoted Marseille pairs whose matched
        // triangles shared only 0.23..0.47% (below the established 1% floor).
        if (firstPlane.IsPrimary && secondPlane.IsPrimary)
            return HasInteriorOverlap(first, second);

        return CoplanarOverlayGeometry.HasInteriorOverlap(
            GetAdmittedTriangle(first, firstPlane),
            GetAdmittedTriangle(second, secondPlane));
    }

    private readonly record struct PlaneKey(int X, int Y, int Z, int Distance);

    private readonly record struct CandidatePlane(
        PlaneKey Key,
        bool NormalFlipped,
        bool IsPrimary,
        float RawDistance);

    private readonly record struct CandidatePlaneInstance(
        Candidate Candidate,
        CandidatePlane Plane);

    private readonly record struct CandidatePairKey(
        PsxFaceInstanceKey First,
        PsxFaceInstanceKey Second)
    {
        internal static CandidatePairKey Create(
            PsxFaceInstanceKey first,
            PsxFaceInstanceKey second)
        {
            return (first.ObjectIndex, first.FaceIndex)
                       .CompareTo((second.ObjectIndex, second.FaceIndex)) <= 0
                ? new CandidatePairKey(first, second)
                : new CandidatePairKey(second, first);
        }
    }

    private readonly record struct CandidateComparison(
        Candidate First,
        Candidate Second,
        CandidatePlane FirstPlane,
        CandidatePlane SecondPlane);

    private sealed record ComparisonDiscovery(
        List<Candidate> Candidates,
        Dictionary<CandidatePairKey, CandidateComparison> Pairs);

    private sealed record ComparisonComponent(
        List<Candidate> Candidates,
        List<CandidateComparison> Pairs,
        IReadOnlyDictionary<CandidatePairKey, CandidateComparison> ComparablePairs);

    private sealed class ComponentBuilder
    {
        internal HashSet<int> CandidateIndices { get; } = [];

        internal List<CandidateComparison> Pairs { get; } = [];
    }

    /// <summary>
    ///     Both rendered triangles retain their own canonical plane and winding
    ///     orientation. The admitting triangle matters for warped quads: a
    ///     secondary-plane match must not inherit the primary triangle's
    ///     back-to-back flag.
    /// </summary>
    private sealed record Candidate(
        int DiscoveryIndex,
        PsxFaceInstanceKey Key,
        PsxFace Face,
        Vector3[] Points,
        float Area,
        Vector3 Centroid,
        Vector3 Min,
        Vector3 Max,
        CandidatePlane PrimaryPlane,
        CandidatePlane? SecondaryPlane);
}

/// <summary>
///     A detected overlay face's per-plane group id plus its draw rank within
///     the group (1 = the first-painted overlay layer; a face stacks one rank
///     above the highest-ranked flagged face it overlaps). The writer emits one
///     mesh per (group, rank) so mutually overlapping overlays get distinct
///     DrawIndex values and stacked separation offsets.
/// </summary>
internal readonly record struct PsxCoplanarOverlayAssignment(int GroupId, int DrawRank);

/// <summary>
///     Why a same-plane pair compared by
///     <see cref="PsxCoplanarOverlayDetector" /> did not produce an overlay.
///     Successful comparisons use a null reason and name their selected
///     overlay in <see cref="PsxCoplanarPairDiagnostic.Overlay" />.
/// </summary>
internal enum PsxCoplanarPairDeclineReason
{
    BackToBackSingleSided,
    SameTextureExactTwin,
    SameTextureWithoutBoundsOverlap,
    SemiTransparentMember,
    SmallerFaceHasInsufficientSharedArea,
    NearEqualWithoutBoundsOverlap,
    NearEqualHasInsufficientSharedArea
}

/// <summary>
///     Why a requested source-face pair never reached the production
///     classifier. These are intentionally separate from
///     <see cref="PsxCoplanarPairDeclineReason" />: no classification rule
///     declined the pair because <c>ClassifyPair</c> was never called.
/// </summary>
internal enum PsxCoplanarPairNotComparedReason
{
    SameFace,
    FirstFaceHasNoCandidate,
    SecondFaceHasNoCandidate,
    BothFacesHaveNoCandidate,
    DifferentPlaneBuckets
}

/// <summary>
///     A detector plane bucket: canonical normal components quantised to
///     1/1000, followed by signed plane distance quantised to 1/100.
/// </summary>
internal readonly record struct PsxCoplanarPlaneKey(int X, int Y, int Z, int Distance);

/// <summary>
///     The primary candidate triangle's production bucket plus, for a quad,
///     the second source triangle's independently measured bucket. Both keys
///     participate in opaque candidate discovery; when a secondary key admits
///     a warped quad, its own rendered triangle must establish shared area.
///     Opaque discovery uses writer-equivalent points, including resolved
///     sprite corners; the separately calibrated transparent-layer collector
///     continues to use its legacy raw candidates.
/// </summary>
internal readonly record struct PsxCoplanarFacePlaneKeys(
    PsxCoplanarPlaneKey Primary,
    PsxCoplanarPlaneKey? Secondary);

/// <summary>
///     The production detector's result for one requested pair. An accepted
///     comparison populates <paramref name="Overlay" />; a compared decline
///     populates <paramref name="DeclineReason" />; a pair production did not
///     compare populates <paramref name="NotComparedReason" />.
/// </summary>
internal readonly record struct PsxCoplanarPairDiagnostic(
    PsxFaceInstanceKey First,
    PsxFaceInstanceKey Second,
    PsxFaceInstanceKey? Overlay,
    PsxCoplanarPairDeclineReason? DeclineReason,
    PsxCoplanarPairNotComparedReason? NotComparedReason,
    PsxCoplanarFacePlaneKeys? FirstPlanes,
    PsxCoplanarFacePlaneKeys? SecondPlanes)
{
    /// <summary>
    ///     Absolute unquantised distance difference between the two triangle
    ///     planes that admitted this production comparison.
    /// </summary>
    internal float? AdmittedPlaneDistanceDelta { get; init; }

    internal bool? FirstAdmissionUsesPrimaryTriangle { get; init; }

    internal bool? SecondAdmissionUsesPrimaryTriangle { get; init; }

    internal float? SharedAreaFraction { get; init; }

    internal float? AdmittedTriangleSharedAreaFraction { get; init; }

    internal float? FirstArea { get; init; }

    internal float? SecondArea { get; init; }
}
