using System.Numerics;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;
using static NeversoftMultitool.Tests.Core.Formats.Ps2Scene.ThawPs2ReplayTriangleDiagnostics;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2ReplayTriangleDiagnosticTests(TestPaths paths)
{
    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

    private string ThawPcSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "SKIN");

    [Theory]
    [InlineData("skater_lasek", 3070)]
    [InlineData("pro_vallely_head", 710)]
    public void Diagnostic_Ps2VsPc_TriangleComparison(string stem, int expectedPcTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");

        var ps2File = Path.Combine(ThawSkinDir, $"{stem}.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, $"{stem}.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), $"PS2 file not found: {stem}");
        Assert.SkipWhen(!File.Exists(pcFile), $"PC file not found: {stem}");

        var pcScene = ThawSceneFile.Parse(pcFile);
        var pcTriangles = new HashSet<(Vector3, Vector3, Vector3)>();
        var pcPositions = new HashSet<Vector3>();
        var pcTrisBySector = new Dictionary<int, int>();
        var pcVertsBySector = new Dictionary<int, int>();
        for (var si = 0; si < pcScene.Sectors.Length; si++)
        {
            var sector = pcScene.Sectors[si];
            var sectorTris = 0;
            var sectorVerts = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            {
                for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
                {
                    var p0 = mesh.Vertices[mesh.FaceIndices[i]].Position;
                    var p1 = mesh.Vertices[mesh.FaceIndices[i + 1]].Position;
                    var p2 = mesh.Vertices[mesh.FaceIndices[i + 2]].Position;
                    pcPositions.Add(p0);
                    pcPositions.Add(p1);
                    pcPositions.Add(p2);
                    sectorVerts.Add(p0);
                    sectorVerts.Add(p1);
                    sectorVerts.Add(p2);

                    if (p0 == p1 || p1 == p2 || p0 == p2)
                        continue;

                    pcTriangles.Add(SortedTriKey(p0, p1, p2));
                    sectorTris++;
                }
            }

            pcTrisBySector[si] = sectorTris;
            pcVertsBySector[si] = sectorVerts.Count;
        }

        var ps2Data = File.ReadAllBytes(ps2File);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(ps2Data);

        var ps2Triangles = new HashSet<(Vector3, Vector3, Vector3)>();
        var ps2TriByKick = new Dictionary<int, List<(Vector3, Vector3, Vector3)>>();
        var ps2Positions = new HashSet<Vector3>();

        foreach (var kick in kicks)
        {
            var kickTris = new List<(Vector3, Vector3, Vector3)>();
            ps2TriByKick[kick.KickIndex] = kickTris;

            foreach (var mesh in kick.Meshes)
            {
                var verts = mesh.Vertices;
                var stripStart = 0;
                var parityBias = mesh.StartsOnOddOutputSlot ? 1 : 0;

                for (var i = 0; i < verts.Length; i++)
                {
                    ps2Positions.Add(verts[i].Position);
                    if (verts[i].IsStripRestart || i - stripStart < 2)
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

                    if (a == b || b == c || a == c)
                        continue;

                    var key = SortedTriKey(a, b, c);
                    ps2Triangles.Add(key);
                    kickTris.Add(key);
                }
            }
        }

        var posToKicks = new Dictionary<Vector3, List<int>>();
        foreach (var kick in kicks)
        {
            foreach (var mesh in kick.Meshes)
            {
                foreach (var position in mesh.Vertices.Select(static vertex => vertex.Position))
                {
                    if (!posToKicks.TryGetValue(position, out var kickList))
                    {
                        kickList = [];
                        posToKicks[position] = kickList;
                    }

                    if (!kickList.Contains(kick.KickIndex))
                        kickList.Add(kick.KickIndex);
                }
            }
        }

        var matched = pcTriangles.Intersect(ps2Triangles).Count();
        var pcOnly = pcTriangles.Except(ps2Triangles).ToList();
        var ps2Only = ps2Triangles.Except(pcTriangles).ToList();
        var pcTriangleDiagnostics = BuildPcTriangleDiagnostics(pcScene);
        var ps2TriangleDiagnostics = BuildPs2TriangleDiagnostics(kicks);
        var ps2TrianglesByMaterial = ps2TriangleDiagnostics.ToLookup(triangle => triangle.MaterialChecksum);
        var pcOnlyTriangleDiagnostics = pcTriangleDiagnostics
            .Where(triangle => !ps2Triangles.Contains(SortedTriKey(triangle.A, triangle.B, triangle.C)))
            .ToList();
        var heuristicMatches = pcOnlyTriangleDiagnostics
            .Select(triangle => EvaluateTriangleMatch(triangle, ps2TriangleDiagnostics, ps2TrianglesByMaterial))
            .OrderByDescending(match => match.GlobalMatch.Metrics.Score)
            .ThenByDescending(match => match.GlobalMatch.Metrics.MaxVertexDistance)
            .ToList();
        var heuristicallyUnmatched = heuristicMatches
            .Where(IsHeuristicallyUnmatched)
            .ToList();
        var unmatchedClusters = ClusterTriangleMatches(heuristicallyUnmatched);

        var lines = new List<string>
        {
            $"=== PS2 vs PC Triangle Diagnostic: {stem} ===",
            $"PC triangles (non-degenerate): {pcTriangles.Count}",
            $"PS2 triangles (non-degenerate): {ps2Triangles.Count}",
            $"Matched: {matched}",
            $"PC-only (missing from PS2): {pcOnly.Count}",
            $"PS2-only (phantom): {ps2Only.Count}",
            $"PC unique positions: {pcPositions.Count}",
            $"PS2 unique positions: {ps2Positions.Count}",
            ""
        };

        lines.Add("=== Missing PC Triangles Analysis ===");
        var allVertsInPs2 = 0;
        var twoVertsInPs2 = 0;
        var oneVertInPs2 = 0;
        var noVertsInPs2 = 0;
        var kickGapCounts = new Dictionary<int, int>();

        foreach (var (p0, p1, p2) in pcOnly)
        {
            var has0 = ps2Positions.Contains(p0);
            var has1 = ps2Positions.Contains(p1);
            var has2 = ps2Positions.Contains(p2);
            var count = (has0 ? 1 : 0) + (has1 ? 1 : 0) + (has2 ? 1 : 0);

            switch (count)
            {
                case 3: allVertsInPs2++; break;
                case 2: twoVertsInPs2++; break;
                case 1: oneVertInPs2++; break;
                case 0: noVertsInPs2++; break;
            }

            if (count != 3)
                continue;

            var k0 = posToKicks.GetValueOrDefault(p0, []);
            var k1 = posToKicks.GetValueOrDefault(p1, []);
            var k2 = posToKicks.GetValueOrDefault(p2, []);
            var sharedKicks = k0.Intersect(k1).Intersect(k2).ToList();
            foreach (var kickIndex in sharedKicks)
                kickGapCounts[kickIndex] = kickGapCounts.GetValueOrDefault(kickIndex) + 1;
        }

        lines.Add($"Missing triangles with 3/3 verts in PS2: {allVertsInPs2} (topology diff)");
        lines.Add($"Missing triangles with 2/3 verts in PS2: {twoVertsInPs2} (1 vert missing)");
        lines.Add($"Missing triangles with 1/3 verts in PS2: {oneVertInPs2}");
        lines.Add($"Missing triangles with 0/3 verts in PS2: {noVertsInPs2}");
        lines.Add("");

        lines.Add("=== Per-Kick Gap Analysis ===");
        foreach (var kick in kicks)
        {
            var gaps = kick.Events.Count(evt => evt.Kind == GsVertexEventKind.Gap);
            var carries = kick.Events.Count(evt => evt.IsBufferedCarry);
            var noKicks = kick.Events.Count(evt => evt.IsNoKick);
            var missingTrisInKick = kickGapCounts.GetValueOrDefault(kick.KickIndex);

            if (gaps == 0 && missingTrisInKick == 0)
                continue;

            lines.Add($"Kick {kick.KickIndex}: setup={kick.SetupIndex} addr={kick.KickPacket.Address} nloop={kick.KickPacket.Nloop}");
            lines.Add($"  gaps={gaps} carries={carries} noKicks={noKicks} tris={kick.TriangleCount} meshes={kick.Meshes.Length}");
            lines.Add($"  missingPcTris (all 3 verts in this kick): {missingTrisInKick}");

            if (gaps > 0)
            {
                var gapAddrs = kick.Events
                    .Where(evt => evt.Kind == GsVertexEventKind.Gap)
                    .Select(evt => evt.FullOutputAddress)
                    .ToList();
                lines.Add($"  gap addresses: [{string.Join(",", gapAddrs)}]");

                var gapRuns = new List<(int Start, int Length)>();
                for (var index = 0; index < gapAddrs.Count; index++)
                {
                    if (index == 0 || gapAddrs[index] - gapAddrs[index - 1] != 3)
                        gapRuns.Add((gapAddrs[index], 1));
                    else
                        gapRuns[^1] = (gapRuns[^1].Start, gapRuns[^1].Length + 1);
                }

                lines.Add($"  gap runs: {gapRuns.Count} (sizes: {string.Join(",", gapRuns.Select(run => run.Length))})");
            }

            lines.Add("");
        }

        lines.Add("=== Cross-Kick Missing Triangles ===");
        var crossKickCount = 0;
        var sameKickCount = 0;
        foreach (var (p0, p1, p2) in pcOnly)
        {
            if (!ps2Positions.Contains(p0) || !ps2Positions.Contains(p1) || !ps2Positions.Contains(p2))
                continue;

            var k0 = posToKicks.GetValueOrDefault(p0, []);
            var k1 = posToKicks.GetValueOrDefault(p1, []);
            var k2 = posToKicks.GetValueOrDefault(p2, []);
            var sharedKicks = k0.Intersect(k1).Intersect(k2).ToList();
            if (sharedKicks.Count > 0)
                sameKickCount++;
            else
                crossKickCount++;
        }

        lines.Add($"All 3 verts in same kick(s): {sameKickCount}");
        lines.Add($"Verts spread across kicks: {crossKickCount}");
        lines.Add("");

        if (ps2Only.Count > 0)
        {
            lines.Add("=== Phantom PS2 Triangles (first 20) ===");
            foreach (var (p0, p1, p2) in ps2Only.Take(20))
            {
                var k0 = posToKicks.GetValueOrDefault(p0, []);
                lines.Add(
                    $"  ({p0.X:F2},{p0.Y:F2},{p0.Z:F2})-({p1.X:F2},{p1.Y:F2},{p1.Z:F2})-({p2.X:F2},{p2.Y:F2},{p2.Z:F2}) kicks=[{string.Join(",", k0)}]");
            }

            lines.Add("");
        }

        lines.Add("=== Summary by Kick Address ===");
        foreach (var addr in new[] { 280, 652 })
        {
            var addrKicks = kicks.Where(kick => kick.KickPacket.Address == addr).ToList();
            var totalGaps = addrKicks.Sum(kick => kick.Events.Count(evt => evt.Kind == GsVertexEventKind.Gap));
            var totalTris = addrKicks.Sum(kick => kick.TriangleCount);
            var totalMissing = addrKicks.Sum(kick => kickGapCounts.GetValueOrDefault(kick.KickIndex));
            lines.Add(
                $"ADDR={addr}: {addrKicks.Count} kicks, {totalTris} tris, {totalGaps} gaps, {totalMissing} missing PC tris in same kick");
        }

        lines.Add("");

        var batches = ThawPs2SkinFile.ReplayBatches(ps2Data);
        var allBatchPositions = new HashSet<Vector3>();
        var totalBatchVerts = 0;
        foreach (var batch in batches)
        {
            totalBatchVerts += batch.VertexCount;
            foreach (var vertexSource in batch.VertexSources)
                allBatchPositions.Add(vertexSource.Position);
        }

        lines.Add("=== VIF Batch Vertex Analysis ===");
        lines.Add($"Total VIF batches: {batches.Count}");
        lines.Add($"Total VIF vertices (raw): {totalBatchVerts}");
        lines.Add($"Unique VIF positions (all batches): {allBatchPositions.Count}");
        lines.Add($"Unique kicked positions: {ps2Positions.Count}");
        lines.Add($"VIF positions not in kicks: {allBatchPositions.Except(ps2Positions).Count()}");
        lines.Add($"PC positions not in VIF: {pcPositions.Except(allBatchPositions).Count()}");
        lines.Add($"PC positions found in VIF: {pcPositions.Intersect(allBatchPositions).Count()}");
        lines.Add("");

        lines.Add("=== PC Sector Breakdown ===");
        lines.Add($"PC sectors: {pcScene.Sectors.Length}");
        for (var sectorIndex = 0; sectorIndex < pcScene.Sectors.Length; sectorIndex++)
        {
            var sector = pcScene.Sectors[sectorIndex];
            var sectorPositions = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            {
                foreach (var vertex in mesh.Vertices)
                    sectorPositions.Add(vertex.Position);
            }

            var inPs2 = sectorPositions.Intersect(allBatchPositions).Count();
            lines.Add($"  Sector {sectorIndex}: ck=0x{sector.Checksum:X8} meshes={sector.Meshes.Length} " +
                      $"tris={pcTrisBySector[sectorIndex]} verts={pcVertsBySector[sectorIndex]} " +
                      $"ps2_match={inPs2}/{sectorPositions.Count} ({100.0 * inPs2 / Math.Max(1, sectorPositions.Count):F0}%)");

            for (var meshIndex = 0; meshIndex < sector.Meshes.Length; meshIndex++)
            {
                var mesh = sector.Meshes[meshIndex];
                var meshPositions = new HashSet<Vector3>();
                foreach (var vertex in mesh.Vertices)
                    meshPositions.Add(vertex.Position);

                var meshInPs2 = meshPositions.Intersect(allBatchPositions).Count();
                var meshTris = mesh.IsPreTriangulated ? mesh.FaceIndices.Length / 3 : mesh.TriangleCount;
                lines.Add(
                    $"    Mesh {meshIndex}: mat=0x{mesh.MaterialChecksum:X8} tris={meshTris} verts={meshPositions.Count} " +
                    $"ps2_match={meshInPs2}/{meshPositions.Count} ({100.0 * meshInPs2 / Math.Max(1, meshPositions.Count):F0}%)");
            }
        }

        lines.Add("");
        lines.Add("=== PS2 Entry Table ===");
        lines.Add($"PS2 kicks: {kicks.Count}");
        lines.Add($"PS2 numObjects: {BitConverter.ToUInt32(ps2Data, 0)}");
        lines.Add($"PS2 totalMeshes2 (entry table entries): {BitConverter.ToUInt32(ps2Data, 8)}");
        lines.Add($"PS2 setup indices used: [{string.Join(",", kicks.Select(kick => kick.SetupIndex).Distinct().OrderBy(index => index))}]");
        lines.Add("");

        var missingPcPositions = pcPositions.Except(allBatchPositions).ToList();
        lines.Add($"=== Missing PC Positions ({missingPcPositions.Count}) ===");
        for (var sectorIndex = 0; sectorIndex < pcScene.Sectors.Length; sectorIndex++)
        {
            var sector = pcScene.Sectors[sectorIndex];
            var sectorPositions = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            {
                foreach (var vertex in mesh.Vertices)
                    sectorPositions.Add(vertex.Position);
            }

            var sectorMissing = sectorPositions.Intersect(missingPcPositions.ToHashSet()).Count();
            if (sectorMissing > 0)
                lines.Add($"  Sector {sectorIndex} contributes {sectorMissing} missing positions");
        }

        lines.Add("");
        foreach (var position in missingPcPositions.OrderBy(point => point.X).ThenBy(point => point.Y).ThenBy(point => point.Z).Take(30))
        {
            var nearest = allBatchPositions.OrderBy(point => Vector3.DistanceSquared(point, position)).First();
            var distance = Vector3.Distance(nearest, position);
            lines.Add(
                $"  ({position.X:F2},{position.Y:F2},{position.Z:F2}) nearest PS2=({nearest.X:F2},{nearest.Y:F2},{nearest.Z:F2}) dist={distance:F4}");
        }

        lines.Add("");
        lines.Add("=== Heuristic Triangle Match Summary ===");
        lines.Add($"Exact-unmatched PC triangles evaluated: {heuristicMatches.Count}");
        lines.Add($"Global best match score <= 0.10: {heuristicMatches.Count(match => match.GlobalMatch.Metrics.Score <= 0.10f)}");
        lines.Add($"Global best match score <= 0.25: {heuristicMatches.Count(match => match.GlobalMatch.Metrics.Score <= 0.25f)}");
        lines.Add($"Heuristically unmatched: {heuristicallyUnmatched.Count} (avgDist > 0.25 or maxDist > 0.50)");
        lines.Add("");
        lines.Add("=== Heuristic Triangle Match Table ===");
        lines.Add(
            "rank\tpcMat\tglobalScore\tglobalAvg\tglobalMax\tglobalCtr\tglobalDot\tglobalArea\tglobalKick\tglobalPs2Mat\tsameScore\tsameAvg\tsameMax\tsameCtr\tsameDot\tsameArea\tpcCentroid\tpcVerts");
        for (var index = 0; index < heuristicMatches.Count; index++)
        {
            var match = heuristicMatches[index];
            var same = match.SameMaterialMatch;
            var sameScore = same is null ? "-" : FormatMetric(same.Metrics.Score);
            var sameAverage = same is null ? "-" : FormatMetric(same.Metrics.AverageVertexDistance);
            var sameMax = same is null ? "-" : FormatMetric(same.Metrics.MaxVertexDistance);
            var sameCentroid = same is null ? "-" : FormatMetric(same.Metrics.CentroidDistance);
            var sameNormal = same is null ? "-" : FormatMetric(same.Metrics.NormalDot);
            var sameArea = same is null ? "-" : FormatMetric(same.Metrics.AreaRatio);
            lines.Add(
                $"{index + 1}\t0x{match.Source.MaterialChecksum:X8}\t{FormatMetric(match.GlobalMatch.Metrics.Score)}\t{FormatMetric(match.GlobalMatch.Metrics.AverageVertexDistance)}\t{FormatMetric(match.GlobalMatch.Metrics.MaxVertexDistance)}\t{FormatMetric(match.GlobalMatch.Metrics.CentroidDistance)}\t{FormatMetric(match.GlobalMatch.Metrics.NormalDot)}\t{FormatMetric(match.GlobalMatch.Metrics.AreaRatio)}\t{match.GlobalMatch.Target.KickIndex}\t0x{match.GlobalMatch.Target.MaterialChecksum:X8}\t{sameScore}\t{sameAverage}\t{sameMax}\t{sameCentroid}\t{sameNormal}\t{sameArea}\t{FormatVector(match.Source.Centroid)}\t{FormatTriangle(match.Source)}");
        }

        lines.Add("");
        lines.Add("=== Heuristically Unmatched Patch Clusters ===");
        lines.Add($"Cluster count: {unmatchedClusters.Count}");
        for (var index = 0; index < unmatchedClusters.Count; index++)
        {
            var cluster = unmatchedClusters[index];
            var bounds = GetTriangleBounds(cluster.Select(match => match.Source));
            var materials = cluster
                .Select(match => $"0x{match.Source.MaterialChecksum:X8}")
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            var kickModes = cluster
                .GroupBy(match => match.GlobalMatch.Target.KickIndex)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Take(3)
                .Select(group => $"{group.Key}:{group.Count()}")
                .ToArray();
            lines.Add(
                $"Cluster {index + 1}: tris={cluster.Count} mats=[{string.Join(",", materials)}] boundsMin={FormatVector(bounds.Min)} boundsMax={FormatVector(bounds.Max)} avgScore={cluster.Average(match => match.GlobalMatch.Metrics.Score):F5} maxScore={cluster.Max(match => match.GlobalMatch.Metrics.Score):F5} nearestKicks=[{string.Join(",", kickModes)}]");

            foreach (var match in cluster
                         .OrderByDescending(item => item.GlobalMatch.Metrics.Score)
                         .ThenByDescending(item => item.GlobalMatch.Metrics.MaxVertexDistance)
                         .Take(12))
            {
                lines.Add(
                    $"  score={match.GlobalMatch.Metrics.Score:F5} avg={match.GlobalMatch.Metrics.AverageVertexDistance:F5} max={match.GlobalMatch.Metrics.MaxVertexDistance:F5} pcMat=0x{match.Source.MaterialChecksum:X8} nearestKick={match.GlobalMatch.Target.KickIndex} nearestPs2Mat=0x{match.GlobalMatch.Target.MaterialChecksum:X8} centroid={FormatVector(match.Source.Centroid)} tri={FormatTriangle(match.Source)}");
            }

            lines.Add("");
        }

        var outputPath = Path.Combine(paths.TestOutputDir!, $"{stem}_tri_diagnostic.txt");
        File.WriteAllLines(outputPath, lines);

        Assert.Equal(expectedPcTriangles, pcTriangles.Count);
    }
}
