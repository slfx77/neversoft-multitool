using System.CommandLine;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace PsxAnalyzer.Commands;

/// <summary>
///     Adjudicates <see cref="PsxCoplanarOverlayDetector" /> results on a
///     single PSX file: for every flagged opaque overlay face, finds its best
///     same-plane partner and reports the in-plane AABB penetration plus the
///     mutual centroid-interior facts, so false positives (edge-adjacent
///     panels that merely touch) are visible at a glance. Written 2026-07-29
///     while pinning the near-equal-branch interior-overlap fix (l2a1
///     394 → 82 flags).
///
///     Usage: PsxAnalyzer overlay-census &lt;file.psx&gt; [--limit N]
/// </summary>
public static class OverlayCensusCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "PSX level file (e.g. l2a1_g.psx)"
        };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Detailed rows to print (default 12; 0 = summary only)",
            DefaultValueFactory = static _ => 12
        };

        var command = new Command(
            "overlay-census",
            "Report opaque coplanar overlay flags with partner-overlap evidence");
        command.Arguments.Add(inputArgument);
        command.Options.Add(limitOption);

        command.SetAction((parseResult, _) =>
        {
            Run(parseResult.GetValue(inputArgument)!, parseResult.GetValue(limitOption));
            return Task.FromResult(0);
        });

        return command;
    }

    private static void Run(string input, int limit)
    {
        var file = PsxMeshFile.Parse(input);
        if (file is null)
        {
            AnsiConsole.MarkupLine("[red]No mesh data.[/]");
            return;
        }

        var overlays = PsxCoplanarOverlayDetector.Find(file);
        var planes = PsxAnalyzerPlanes.Collect(file);
        AnsiConsole.MarkupLine(
            $"[bold]{Path.GetFileName(input)}[/]: {overlays.Count} overlay faces "
            + $"across {planes.Count} candidate planes");

        var printed = 0;
        var withInteriorPartner = 0;
        foreach (var key in overlays.OrderBy(static key => (key.ObjectIndex, key.FaceIndex)))
        {
            var (flagged, partners) = FindFaceAndPlane(planes, key);
            if (flagged is null || partners is null)
                continue;

            var overlapping = partners
                .Where(candidate => candidate.Key != key)
                .Select(candidate => (Geometry: candidate, Penetration: InPlanePenetration(flagged, candidate)))
                .Where(static pair => pair.Penetration > 0f)
                .OrderByDescending(static pair => pair.Penetration)
                .ToList();
            var best = overlapping.Count > 0
                ? (ValueTuple<PsxAnalyzerPlanes.FaceGeometry, float>?)(overlapping[0].Geometry, overlapping[0].Penetration)
                : null;
            var interior = overlapping.Any(pair =>
                PointInsideFace(flagged.Centroid, pair.Geometry.Points)
                || PointInsideFace(pair.Geometry.Centroid, flagged.Points));
            if (interior)
                withInteriorPartner++;

            if (printed >= limit)
                continue;
            printed++;
            var partnerText = best is { } found
                ? $"partner obj {found.Item1.Key.ObjectIndex} face {found.Item1.Key.FaceIndex} "
                  + $"tex 0x{found.Item1.Face.TextureHash:X8} penetration {found.Item2:F2} "
                  + $"centroidInterior={interior}"
                : "NO overlapping partner";
            AnsiConsole.WriteLine(
                $"  obj {key.ObjectIndex} face {key.FaceIndex} tex 0x{flagged.Face.TextureHash:X8} "
                + $"c=({flagged.Centroid.X:F1},{flagged.Centroid.Y:F1},{flagged.Centroid.Z:F1}) "
                + $"ext=({flagged.Max.X - flagged.Min.X:F1},{flagged.Max.Y - flagged.Min.Y:F1},"
                + $"{flagged.Max.Z - flagged.Min.Z:F1}) -> {partnerText}");
        }

        AnsiConsole.MarkupLine(
            $"[bold]{withInteriorPartner}/{overlays.Count}[/] flagged faces have a same-plane "
            + "partner with interior (centroid-inside) overlap");
    }

    private static (PsxAnalyzerPlanes.FaceGeometry? Flagged, List<PsxAnalyzerPlanes.FaceGeometry>? Plane) FindFaceAndPlane(
        Dictionary<(int, int, int, int), List<PsxAnalyzerPlanes.FaceGeometry>> planes,
        PsxFaceInstanceKey key)
    {
        foreach (var candidates in planes.Values)
        {
            var flagged = candidates.Find(candidate => candidate.Key == key);
            if (flagged != null)
                return (flagged, candidates);
        }

        return (null, null);
    }

    /// <summary>Smallest positive per-axis AABB penetration over the two in-plane axes.</summary>
    private static float InPlanePenetration(PsxAnalyzerPlanes.FaceGeometry first, PsxAnalyzerPlanes.FaceGeometry second)
    {
        var penetrations = new List<float>(3);
        for (var axis = 0; axis < 3; axis++)
        {
            var penetration = MathF.Min(first.Max[axis], second.Max[axis])
                              - MathF.Max(first.Min[axis], second.Min[axis]);
            if (penetration < -0.1f)
                return 0f;
            penetrations.Add(penetration);
        }

        penetrations.Sort();
        return MathF.Max(penetrations[1], 0f);
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

}
