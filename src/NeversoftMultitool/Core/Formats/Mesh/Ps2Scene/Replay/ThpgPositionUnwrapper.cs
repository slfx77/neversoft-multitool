using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

/// <summary>
///     Proving Ground (2007) .skin.ps2 files re-encode V3_16 vertex positions as Q4.12
///     fixed point (position × 4096) inside the same VIF framing THAW/Project 8 use for
///     Q12.4 (position × 16). Q4.12 spans only ±8 units, so character-scale meshes wrap
///     mod 65536 and the integer "band" (multiple of 16 units) is not stored — the VU1
///     microprogram reconstructs it at runtime. Verified against the Project 8 copy of
///     the same character (gped_bam ships in both games; 2,769 oracle vertices matched
///     with max residual 1/32 unit, and bands proved spatially continuous while bone
///     indices span up to six bands — so reconstruction must come from mesh connectivity,
///     not bone data).
///     <para>
///         Detection: decode the interleaved V3_16 batches at Q12.4 and compare the raw
///         extent against the header bounding sphere — Q4.12 files span the full sint16
///         range (±2048 units at ÷16) while real Q12.4 content stays inside the sphere.
///         Reconstruction: phase-unwrap over mesh connectivity. Strip-adjacent vertices
///         and exact fine-position duplicates are near each other in space, so each edge
///         fixes the relative band between its endpoints; a weighted union-find merges
///         edges in confidence order (most-reliable first), and each connected
///         component's absolute band is chosen so the component sits inside the header
///         bounding sphere, tie-broken toward the sphere centre.
///     </para>
/// </summary>
internal static class ThpgPositionUnwrapper
{
    /// <summary>Q4.12 wrap period in world units (65536 / 4096).</summary>
    private const float BandSize = 16f;

    /// <summary>
    ///     Per-section bounding box parsed from the 80-byte gap-chunk records between the
    ///     entry table and the VIF stream. Each record carries the section's VIF data
    ///     offset (+0x14), bbox half-extents (+0x28) and bbox centre (+0x38) — the
    ///     absolute anchors the Q4.12 encoding relies on.
    /// </summary>
    internal readonly record struct SectionBounds(int VifOffset, Vector3 Center, Vector3 HalfExtent);

    /// <summary>
    ///     Parses the per-section bbox records from the gap region. Returns an empty list
    ///     when the region does not match the expected 80-byte record layout (callers
    ///     fall back to bounding-sphere placement).
    /// </summary>
    internal static List<SectionBounds> ReadSectionBounds(byte[] data)
    {
        var result = new List<SectionBounds>();
        if (data.Length < 0x20)
            return result;

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes = BitConverter.ToUInt32(data, 8);
        if (totalMeshes == 0 || totalMeshes > 500)
            return result;

        var recordBase = (int)(32 + numObjects * 8 + totalMeshes * 64);
        var previousOffset = 0;
        for (var i = 0; i < totalMeshes; i++)
        {
            var off = recordBase + i * 80;
            if (off + 80 > data.Length)
                return [];

            var vifOffset = BitConverter.ToInt32(data, off + 0x14);
            var center = new Vector3(
                BitConverter.ToSingle(data, off + 0x38),
                BitConverter.ToSingle(data, off + 0x3C),
                BitConverter.ToSingle(data, off + 0x40));
            var halfExtent = new Vector3(
                BitConverter.ToSingle(data, off + 0x28),
                BitConverter.ToSingle(data, off + 0x2C),
                BitConverter.ToSingle(data, off + 0x30));

            // Sanity: offsets ascend within the file; bbox values are finite and modest.
            if (vifOffset <= previousOffset || vifOffset >= data.Length ||
                !IsFinite(center) || !IsFinite(halfExtent) ||
                halfExtent.X < 0 || halfExtent.Y < 0 || halfExtent.Z < 0 ||
                MathF.Max(halfExtent.X, MathF.Max(halfExtent.Y, halfExtent.Z)) > 4096)
            {
                return [];
            }

            previousOffset = vifOffset;
            result.Add(new SectionBounds(vifOffset, center, halfExtent));
        }

        return result;
    }

    private static bool IsFinite(Vector3 v)
    {
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    /// <summary>
    ///     Detects the Proving Ground Q4.12 position variant by scanning the interleaved
    ///     vertex batches (STCYCL CL=3 WL=1 + UNPACK V3_16/V3_8) byte-wise — gap chunks
    ///     between meshes cannot desync a signature scan — and comparing the maximum
    ///     |sint16| against the 32-byte header's bounding sphere.
    /// </summary>
    internal static bool UsesQ412Positions(byte[] data, int scanStart, int scanEnd)
    {
        if (data.Length < 0x20)
            return false;

        var extent = ReadBoundingSphereExtent(data);
        if (extent <= 0)
            return false;

        var maxAbs = 0;
        var i = Math.Max(scanStart, 0);
        var last = Math.Min(scanEnd, data.Length) - 8;
        while (i < last)
        {
            // STCYCL CL=3 WL=1 followed by UNPACK V3_16 (0x69, mask bit tolerated).
            if (data[i] == 3 && data[i + 1] == 1 && data[i + 3] == 0x01 &&
                (data[i + 7] & 0x6F) == 0x69)
            {
                int n = data[i + 6];
                if (n == 0) n = 256;
                var posOff = i + 8;
                var posSize = ((6 * n + 3) >> 2) << 2;
                var nrmOff = posOff + posSize;
                // Require the V3_8 normals unpack right after — the interleaved-batch
                // signature — so stray V3_16 unpacks elsewhere don't contribute.
                if (nrmOff + 4 <= data.Length && (data[nrmOff + 3] & 0x6F) == 0x6A)
                {
                    var end = Math.Min(posOff + 6 * n, data.Length - 1);
                    for (var p = posOff; p + 1 < end; p += 2)
                    {
                        var v = Math.Abs((int)BitConverter.ToInt16(data, p));
                        if (v > maxAbs) maxAbs = v;
                    }

                    i = nrmOff;
                    continue;
                }
            }

            i += 4;
        }

        // Q12.4 content stays within the bounding sphere; Q4.12 wraps across the full
        // sint16 range. Factor 2 keeps loose bounding spheres from false-positives.
        return maxAbs / 16f > 2f * extent;
    }

    /// <summary>
    ///     Reconstructs absolute positions for meshes whose vertices were decoded at the
    ///     Q4.12 scale (fine positions in [-8, 8)). Mutates the mesh vertex arrays.
    ///     <paramref name="sectionOfMesh" /> maps each mesh to its per-section bbox in
    ///     <paramref name="sections" /> (-1 = unknown; containment constraint skipped).
    /// </summary>
    internal static void Unwrap(
        IReadOnlyList<Ps2Mesh> meshes,
        byte[] header,
        IReadOnlyList<int>? sectionOfMesh = null,
        IReadOnlyList<SectionBounds>? sections = null)
    {
        var center = new Vector3(
            BitConverter.ToSingle(header, 0x10),
            BitConverter.ToSingle(header, 0x14),
            BitConverter.ToSingle(header, 0x18));
        var radius = BitConverter.ToSingle(header, 0x1C);
        if (!float.IsFinite(radius) || radius <= 0)
            return;

        var meshStart = new int[meshes.Count];
        var total = 0;
        for (var m = 0; m < meshes.Count; m++)
        {
            meshStart[m] = total;
            total += meshes[m].Vertices.Length;
        }

        if (total == 0)
            return;

        var fine = new Vector3[total];
        var vertexSection = new int[total];
        for (var m = 0; m < meshes.Count; m++)
        {
            var verts = meshes[m].Vertices;
            var section = sectionOfMesh != null && m < sectionOfMesh.Count ? sectionOfMesh[m] : -1;
            for (var i = 0; i < verts.Length; i++)
            {
                fine[meshStart[m] + i] = verts[i].Position;
                vertexSection[meshStart[m] + i] = sections is { Count: > 0 } ? section : -1;
            }
        }

        var uf = new BandUnionFind(total);

        // 1) Weld joins: identical quantized fine triples within the SAME section are
        //    the same source position (stitch copies between batches share a per-setup
        //    buffer, so stitches never cross sections), band delta 0. Cross-section
        //    matches are excluded: identical small detail meshes instanced at different
        //    body locations (e.g. a stud on a shoe and on a hat) have identical fine
        //    values but different bands, and welding them drags one to the wrong band.
        //    (Cross-section welds by carry provenance were tried and made placement
        //    worse — boundary kicks re-render prior geometry under the next section's
        //    label and the merged components then fight their section boxes.)
        var bySectionKey = new Dictionary<(int X, int Y, int Z, int Section), int>(total);
        for (var idx = 0; idx < total; idx++)
        {
            var q = Quantize(fine[idx]);
            var sectionKey = (q.X, q.Y, q.Z, vertexSection[idx]);
            if (bySectionKey.TryGetValue(sectionKey, out var representative))
                uf.Union(representative, idx, 0, 0, 0);
            else
                bySectionKey[sectionKey] = idx;
        }

        // 2) Strip-adjacency edges, most-confident first. Consecutive strip vertices are
        //    triangle-edge neighbours (true distance well under the ±8 wrap half-period)
        //    except across an ADC restart, where the strip jumps to a new surface region.
        var edges = new List<(int A, int B, int Dx, int Dy, int Dz, float MaxResid)>();
        for (var m = 0; m < meshes.Count; m++)
        {
            var verts = meshes[m].Vertices;
            for (var i = 1; i < verts.Length; i++)
            {
                if (verts[i].IsStripRestart)
                    continue;

                var a = meshStart[m] + i - 1;
                var b = meshStart[m] + i;
                var (dx, rx) = BandDelta(fine[a].X, fine[b].X);
                var (dy, ry) = BandDelta(fine[a].Y, fine[b].Y);
                var (dz, rz) = BandDelta(fine[a].Z, fine[b].Z);
                edges.Add((a, b, dx, dy, dz, MathF.Max(rx, MathF.Max(ry, rz))));
            }
        }

        // Residual near the ±8 boundary means the rounded delta is ambiguous; merging
        // confident edges first lets the spanning tree avoid them entirely.
        edges.Sort(static (l, r) => l.MaxResid.CompareTo(r.MaxResid));
        foreach (var (a, b, dx, dy, dz, _) in edges)
            uf.Union(a, b, dx, dy, dz);

        // 3) Resolve each component's absolute band via ResolveComponentBand:
        //    per-vertex containment in the per-section bbox records (exact authored
        //    bounds) + bone-cluster alignment with already-placed components, with the
        //    header bounding sphere as the last-resort fallback.
        var components = new Dictionary<int, List<int>>();
        for (var idx = 0; idx < total; idx++)
        {
            var root = uf.Find(idx);
            if (!components.TryGetValue(root, out var list))
            {
                list = [];
                components[root] = list;
            }

            list.Add(idx);
        }

        // Diagnostic switch (THPG_UNWRAP_DBG=1): component structure + placement
        // decisions, for continuing the residual band-placement work (see backlog).
        var debug = Environment.GetEnvironmentVariable("THPG_UNWRAP_DBG") == "1";
        if (debug)
        {
            Console.Error.WriteLine(
                $"[unwrap] meshes={meshes.Count} verts={total} components={components.Count} sections={sections?.Count ?? 0} " +
                $"sizes=[{string.Join(",", components.Values.Select(static c => c.Count).OrderByDescending(static c => c).Take(20))}]");
        }

        var normals = new Vector3[total];
        for (var m = 0; m < meshes.Count; m++)
        {
            var verts = meshes[m].Vertices;
            for (var i = 0; i < verts.Length; i++)
                normals[meshStart[m] + i] = verts[i].Normal;
        }

        var unwrapped = new Vector3[total];
        var placedGrid = new Dictionary<(int X, int Y, int Z), List<(Vector3 Position, Vector3 Normal)>>();
        var sectionPlacedBounds = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        foreach (var component in components.Values.OrderByDescending(static c => c.Count))
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var centroid = Vector3.Zero;
            foreach (var idx in component)
            {
                var (bx, by, bz) = uf.BandOf(idx);
                var p = fine[idx] + BandSize * new Vector3(bx, by, bz);
                unwrapped[idx] = p;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                centroid += p;
            }

            centroid /= component.Count;

            var (gx, gy, gz) = ResolveComponentShift(
                component, unwrapped, normals, vertexSection, sections, placedGrid,
                sectionPlacedBounds, centroid, min, max, center, radius);
            var shift = BandSize * new Vector3(gx, gy, gz);
            if (debug)
            {
                var sectionHistogram = component
                    .GroupBy(idx => vertexSection[idx])
                    .Select(static g => $"{g.Key}x{g.Count()}");
                Console.Error.WriteLine(
                    $"[unwrap] comp n={component.Count} bbox=({min.X:F1},{min.Y:F1},{min.Z:F1})..({max.X:F1},{max.Y:F1},{max.Z:F1}) " +
                    $"shift=({shift.X:F0},{shift.Y:F0},{shift.Z:F0}) sections=[{string.Join(",", sectionHistogram)}]");
            }

            foreach (var idx in component)
            {
                unwrapped[idx] += shift;
                var cell = Cell(unwrapped[idx]);
                if (!placedGrid.TryGetValue(cell, out var points))
                {
                    points = [];
                    placedGrid[cell] = points;
                }

                points.Add((unwrapped[idx], normals[idx]));

                var section = vertexSection[idx];
                if (section >= 0)
                {
                    sectionPlacedBounds[section] = sectionPlacedBounds.TryGetValue(section, out var bounds)
                        ? (Vector3.Min(bounds.Min, unwrapped[idx]), Vector3.Max(bounds.Max, unwrapped[idx]))
                        : (unwrapped[idx], unwrapped[idx]);
                }
            }
        }

        for (var m = 0; m < meshes.Count; m++)
        {
            var verts = meshes[m].Vertices;
            for (var i = 0; i < verts.Length; i++)
                verts[i] = WithPosition(verts[i], unwrapped[meshStart[m] + i]);
        }
    }

    private const int GridCellSize = 4;
    private const float ProximityCap = 12f;

    /// <summary>
    ///     Chooses the whole-component 16-unit shift. Per axis, candidates are the
    ///     shifts minimizing section-bbox containment violations (the per-section
    ///     records are exact authored bounds; ties within a small epsilon survive).
    ///     When more than one candidate tuple remains — thin or small pieces that fit
    ///     a wide section box at several bands, like hands inside the arms box — the
    ///     tuple that places the piece closest to already-placed geometry wins: a
    ///     character is one connected body, so the correct band makes seams touch
    ///     while wrong bands leave the piece floating a full band away.
    /// </summary>
    private static (int X, int Y, int Z) ResolveComponentShift(
        List<int> component,
        Vector3[] unwrapped,
        Vector3[] normals,
        int[] vertexSection,
        IReadOnlyList<SectionBounds>? sections,
        Dictionary<(int X, int Y, int Z), List<(Vector3 Position, Vector3 Normal)>> placedGrid,
        Dictionary<int, (Vector3 Min, Vector3 Max)> sectionPlacedBounds,
        Vector3 centroid,
        Vector3 min,
        Vector3 max,
        Vector3 sphereCenter,
        float sphereRadius)
    {
        Span<int> counts = stackalloc int[3];
        var candidates = new int[3][];
        var mislabeled = false;
        for (var axis = 0; axis < 3; axis++)
        {
            (candidates[axis], var bestAvgViolation) = AxisCandidates(
                component, unwrapped, vertexSection, sections, sectionPlacedBounds,
                AxisOf(centroid, axis), AxisOf(min, axis), AxisOf(max, axis),
                AxisOf(sphereCenter, axis), sphereRadius, axis);
            counts[axis] = candidates[axis].Length;
            mislabeled |= bestAvgViolation > 2f;
        }

        if (mislabeled)
        {
            // The component's section labels contradict every candidate placement —
            // boundary kicks can render geometry under the following section's label.
            // Ignore the labels and place by coincidence/contact with placed geometry.
            for (var axis = 0; axis < 3; axis++)
            {
                var seed = (int)MathF.Round((AxisOf(sphereCenter, axis) - AxisOf(centroid, axis)) / BandSize);
                candidates[axis] = [.. Enumerable.Range(seed - 4, 9)];
            }
        }
        else if (counts[0] == 1 && counts[1] == 1 && counts[2] == 1)
        {
            return (candidates[0][0], candidates[1][0], candidates[2][0]);
        }

        var debug = Environment.GetEnvironmentVariable("THPG_UNWRAP_DBG") == "1";

        // Joint scoring over the ambiguous tuples. Primary signal: EXACT coincidence
        // count — boundary kicks re-render earlier geometry verbatim, so at the correct
        // shift their vertices land exactly (same quantized fine values) on placed
        // vertices. A mirror twin never coincides exactly (mirrored fine values differ),
        // which is what defeated plain nearest-distance scoring here. Contact score
        // breaks coincidence ties.
        var best = (X: candidates[0][0], Y: candidates[1][0], Z: candidates[2][0]);
        var bestCoincidence = -1;
        var bestScore = float.MaxValue;
        foreach (var gx in candidates[0])
        {
            foreach (var gy in candidates[1])
            {
                foreach (var gz in candidates[2])
                {
                    var offset = BandSize * new Vector3(gx, gy, gz);
                    var score = 0f;
                    var coincidence = 0;
                    var step = Math.Max(1, component.Count / 400);
                    for (var i = 0; i < component.Count; i += step)
                    {
                        var idx = component[i];
                        var (contact, exact) = ContactScore(unwrapped[idx] + offset, normals[idx], placedGrid);
                        score += contact;
                        if (exact)
                            coincidence++;
                    }

                    if (debug && component.Count >= 100)
                    {
                        Console.Error.WriteLine(
                            $"[unwrap]   n={component.Count} candidate g=({gx},{gy},{gz}) " +
                            $"coincide={coincidence} contact={score / Math.Max(1, component.Count / step):F2}/vert");
                    }

                    if (coincidence > bestCoincidence ||
                        (coincidence == bestCoincidence && score < bestScore))
                    {
                        bestCoincidence = coincidence;
                        bestScore = score;
                        best = (gx, gy, gz);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    ///     Per-axis candidate shifts: minimizers of summed section-bbox containment
    ///     violation MINUS a coverage bonus. The stored boxes are the exact authored
    ///     extents of each section's geometry, so the pieces that define a box face
    ///     (the hands at the arms box's ±X faces, the hair at the head box's top) are
    ///     the only ones that can close the gap between the placed union and the
    ///     stored face — rewarding that closes the mirror-twin ambiguity exactly.
    ///     Ties within +0.5 units kept, capped at 4 candidates.
    /// </summary>
    private static (int[] Candidates, float BestAvgViolation) AxisCandidates(
        List<int> component,
        Vector3[] unwrapped,
        int[] vertexSection,
        IReadOnlyList<SectionBounds>? sections,
        Dictionary<int, (Vector3 Min, Vector3 Max)> sectionPlacedBounds,
        float centroid,
        float min,
        float max,
        float sphereCenter,
        float sphereRadius,
        int axis)
    {
        const float containmentSlack = 1.0f;
        const float coverageWeight = 25f;

        var seedTarget = sphereCenter;
        if (sections is { Count: > 0 })
        {
            var sum = 0f;
            var n = 0;
            foreach (var idx in component)
            {
                var section = vertexSection[idx];
                if (section < 0 || section >= sections.Count)
                    continue;
                sum += AxisOf(sections[section].Center, axis);
                n++;
            }

            if (n > 0)
                seedTarget = sum / n;
        }

        var seed = (int)MathF.Round((seedTarget - centroid) / BandSize);
        var scored = new List<(int G, float Score, float Containment)>(9);
        var labeledCount = 0;
        for (var g = seed - 4; g <= seed + 4; g++)
        {
            var offset = BandSize * g;
            var containment = 0f;
            var coverageGain = 0f;
            labeledCount = 0;
            if (sections is { Count: > 0 })
            {
                // Per-section extremes this candidate would contribute.
                var extremes = new Dictionary<int, (float Min, float Max)>();
                foreach (var idx in component)
                {
                    var section = vertexSection[idx];
                    if (section < 0 || section >= sections.Count)
                        continue;

                    var p = AxisOf(unwrapped[idx], axis) + offset;
                    var c = AxisOf(sections[section].Center, axis);
                    var h = AxisOf(sections[section].HalfExtent, axis) + containmentSlack;
                    containment += MathF.Max(0, MathF.Abs(p - c) - h);
                    labeledCount++;
                    extremes[section] = extremes.TryGetValue(section, out var e)
                        ? (MathF.Min(e.Min, p), MathF.Max(e.Max, p))
                        : (p, p);
                }

                foreach (var (section, (compMin, compMax)) in extremes)
                {
                    var storedLo = AxisOf(sections[section].Center, axis) - AxisOf(sections[section].HalfExtent, axis);
                    var storedHi = AxisOf(sections[section].Center, axis) + AxisOf(sections[section].HalfExtent, axis);
                    var placedLo = float.MaxValue;
                    var placedHi = float.MinValue;
                    if (sectionPlacedBounds.TryGetValue(section, out var bounds))
                    {
                        placedLo = AxisOf(bounds.Min, axis);
                        placedHi = AxisOf(bounds.Max, axis);
                    }

                    // Gap between the placed union and the stored faces, before vs after.
                    var gapBefore = MathF.Max(0, MathF.Min(placedLo - storedLo, 32f)) +
                                    MathF.Max(0, MathF.Min(storedHi - placedHi, 32f));
                    var gapAfter = MathF.Max(0, MathF.Min(MathF.Min(placedLo, compMin) - storedLo, 32f)) +
                                   MathF.Max(0, MathF.Min(storedHi - MathF.Max(placedHi, compMax), 32f));
                    coverageGain += gapBefore - gapAfter;
                }
            }
            else
            {
                var lo = min + offset;
                var hi = max + offset;
                containment = component.Count * (
                    MathF.Max(0, hi - (sphereCenter + sphereRadius)) +
                    MathF.Max(0, sphereCenter - sphereRadius - lo));
            }

            scored.Add((g, containment - coverageWeight * coverageGain, containment));
        }

        var bestScore = scored.Min(static s => s.Score);
        var bestAvgViolation = labeledCount > 0
            ? scored.Min(static s => s.Containment) / labeledCount
            : 0f;
        var candidates = scored
            .Where(s => s.Score <= bestScore + 0.5f)
            .OrderBy(s => Math.Abs(s.G - seed))
            .Take(4)
            .Select(static s => s.G)
            .ToArray();
        return (candidates, bestAvgViolation);
    }

    private static (int X, int Y, int Z) Cell(Vector3 p)
    {
        return (
            (int)MathF.Floor(p.X / GridCellSize),
            (int)MathF.Floor(p.Y / GridCellSize),
            (int)MathF.Floor(p.Z / GridCellSize));
    }

    /// <summary>
    ///     Proximity-with-chirality contact score against the placed geometry (lower =
    ///     better placement), plus an exact-coincidence flag. Plain nearest distance
    ///     would prefer superimposing a piece on its mirror twin (the right glove sits
    ///     perfectly "close" on top of the left glove); interpenetrating contact with
    ///     disagreeing normals is physically impossible on a body, so it is penalized
    ///     above the far-distance cap, while close contact with aligned normals (seams,
    ///     layered clothing) stays cheap. Exact coincidence (same quantized fine value —
    ///     the boundary-kick re-render signature) is reported separately so callers can
    ///     prioritize it.
    /// </summary>
    private static (float Score, bool ExactCoincidence) ContactScore(
        Vector3 p,
        Vector3 normal,
        Dictionary<(int X, int Y, int Z), List<(Vector3 Position, Vector3 Normal)>> placedGrid)
    {
        if (placedGrid.Count == 0)
            return (0f, false);

        var (cx, cy, cz) = Cell(p);
        var bestSq = ProximityCap * ProximityCap;
        var bestDot = 0f;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!placedGrid.TryGetValue((cx + dx, cy + dy, cz + dz), out var points))
                        continue;

                    foreach (var (q, n) in points)
                    {
                        var d = Vector3.DistanceSquared(p, q);
                        if (d < bestSq)
                        {
                            bestSq = d;
                            bestDot = Vector3.Dot(normal, n);
                        }
                    }
                }
            }
        }

        // Q4.12 quantization step is 1/4096 ≈ 0.00024; anything under 0.01 units can
        // only be the same encoded value re-rendered.
        var exact = bestSq < 0.01f * 0.01f;
        var distance = MathF.Sqrt(bestSq);
        if (!exact && distance < 1.5f && bestDot < 0.3f)
            return (ProximityCap + 6f, false); // interpenetration with disagreeing normals

        return (distance, exact);
    }

    private static float AxisOf(Vector3 v, int axis)
    {
        return axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z
        };
    }

    private static (int X, int Y, int Z) Quantize(Vector3 fine)
    {
        return (
            (int)MathF.Round(fine.X * 4096f),
            (int)MathF.Round(fine.Y * 4096f),
            (int)MathF.Round(fine.Z * 4096f));
    }

    /// <summary>
    ///     Band delta between two fine coordinates: the integer k minimizing
    ///     |fineA - fineB + 16k|, plus the leftover residual (0 = certain, →8 = ambiguous).
    /// </summary>
    private static (int Delta, float Residual) BandDelta(float fineA, float fineB)
    {
        var diff = fineA - fineB;
        var delta = (int)MathF.Round(diff / BandSize);
        return (delta, MathF.Abs(diff - BandSize * delta));
    }

    private static Ps2Vertex WithPosition(in Ps2Vertex v, Vector3 position)
    {
        return new Ps2Vertex(
            position,
            v.Normal,
            v.R,
            v.G,
            v.B,
            v.A,
            v.U,
            v.V,
            v.HasNormal,
            v.HasColor,
            v.HasUV,
            v.IsStripRestart,
            v.BoneIndex0,
            v.BoneIndex1,
            v.BoneIndex2,
            v.BoneWeight0,
            v.BoneWeight1,
            v.BoneWeight2,
            v.HasSkinData);
    }

    private static float ReadBoundingSphereExtent(byte[] data)
    {
        var cx = BitConverter.ToSingle(data, 0x10);
        var cy = BitConverter.ToSingle(data, 0x14);
        var cz = BitConverter.ToSingle(data, 0x18);
        var radius = BitConverter.ToSingle(data, 0x1C);
        if (!float.IsFinite(cx) || !float.IsFinite(cy) || !float.IsFinite(cz) ||
            !float.IsFinite(radius) || radius <= 0)
        {
            return 0;
        }

        return MathF.Max(MathF.Max(MathF.Abs(cx), MathF.Abs(cy)), MathF.Abs(cz)) + radius;
    }

    /// <summary>
    ///     Union-find tracking each node's integer band offset relative to its root
    ///     (a "potential difference" union-find): Band(node) − Band(root) per axis.
    /// </summary>
    private sealed class BandUnionFind(int count)
    {
        private readonly int[] _parent = CreateIdentity(count);
        private readonly byte[] _rank = new byte[count];
        private readonly int[] _dx = new int[count];
        private readonly int[] _dy = new int[count];
        private readonly int[] _dz = new int[count];

        public int Find(int x)
        {
            if (_parent[x] == x)
                return x;

            var root = Find(_parent[x]);
            if (_parent[x] != root)
            {
                _dx[x] += _dx[_parent[x]];
                _dy[x] += _dy[_parent[x]];
                _dz[x] += _dz[_parent[x]];
                _parent[x] = root;
            }

            return root;
        }

        public (int X, int Y, int Z) BandOf(int x)
        {
            Find(x);
            return (_dx[x], _dy[x], _dz[x]);
        }

        /// <summary>Merge with constraint Band(b) = Band(a) + (dx, dy, dz).</summary>
        public void Union(int a, int b, int dx, int dy, int dz)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA == rootB)
                return; // Later (less confident) edges never override earlier constraints.

            // Band(b) − Band(rootB) = _d[b]; requirement: Band(b) = Band(a) + d.
            // Attach rootB under rootA: Band(rootB) = Band(a) + d − _d[b].
            var offX = _dx[a] + dx - _dx[b];
            var offY = _dy[a] + dy - _dy[b];
            var offZ = _dz[a] + dz - _dz[b];

            if (_rank[rootA] < _rank[rootB])
            {
                // Attach rootA under rootB instead: Band(rootA) = −off relative to rootB.
                _parent[rootA] = rootB;
                _dx[rootA] = -offX;
                _dy[rootA] = -offY;
                _dz[rootA] = -offZ;
                return;
            }

            _parent[rootB] = rootA;
            _dx[rootB] = offX;
            _dy[rootB] = offY;
            _dz[rootB] = offZ;
            if (_rank[rootA] == _rank[rootB])
                _rank[rootA]++;
        }

        private static int[] CreateIdentity(int count)
        {
            var identity = new int[count];
            for (var i = 0; i < count; i++)
                identity[i] = i;
            return identity;
        }
    }
}
