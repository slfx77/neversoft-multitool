using System.Globalization;
using NeversoftMultitool.Core.Rendering;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>Which pixel of the rendered frame to shoot the diagnostic ray through.</summary>
internal readonly record struct ProbeRequest(int PixelX, int PixelY);

/// <summary>
///     Prints every surface a probe ray crossed, with the gap between consecutive hits.
/// </summary>
/// <remarks>
///     The gap column is the point of the whole report: two surfaces a fraction of a unit
///     apart is what a z-fighting complaint looks like from the outside, and printing the
///     names beside the distances turns "this looks wrong" into two node names.
/// </remarks>
internal static class ProbeReporter
{
    /// <summary>Deep rays through a level can cross dozens of surfaces; show the near ones.</summary>
    internal const int MaxReportedHits = 16;

    /// <summary>Column width cap so one long generated name cannot wreck the alignment.</summary>
    private const int MaxNameColumn = 44;

    internal static void Report(
        string file, ViewPose pose, ProbeRequest request, IReadOnlyList<ProbeHit> hits)
    {
        var direction = ViewProbe.RayDirection(pose, request.PixelX, request.PixelY);

        AnsiConsole.MarkupLine(
            $"[bold]Probe[/] {Markup.Escape(Path.GetFileName(file))} " +
            $"[grey]pixel {request.PixelX},{request.PixelY} of {pose.Width}x{pose.Height}[/]");
        AnsiConsole.MarkupLine(
            $"  eye {Markup.Escape(FormatVector(pose.Eye.X, pose.Eye.Y, pose.Eye.Z))}  " +
            $"dir {Markup.Escape(FormatVector(direction.X, direction.Y, direction.Z))}");

        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine("  [yellow]nothing along the ray[/]");
            return;
        }

        var shown = Math.Min(hits.Count, MaxReportedHits);
        AnsiConsole.MarkupLine($"  [green]{hits.Count}[/] surface(s) along the ray, nearest first:");

        var nameWidth = 0;
        for (var i = 0; i < shown; i++)
            nameWidth = Math.Max(nameWidth, DescribeName(hits[i]).Length);
        nameWidth = Math.Min(nameWidth, MaxNameColumn);

        for (var i = 0; i < shown; i++)
        {
            var hit = hits[i];
            var gap = i == 0
                ? new string(' ', 11)
                : $"(+{Distance(hit.Distance - hits[i - 1].Distance)})".PadRight(11);

            var name = DescribeName(hit);
            if (name.Length > MaxNameColumn)
                name = name[..(MaxNameColumn - 1)] + "…";

            AnsiConsole.MarkupLine(
                $"   {(i + 1).ToString(CultureInfo.InvariantCulture),2}. " +
                $"[cyan]{Distance(hit.Distance),12}[/] " +
                $"[yellow]{Markup.Escape(gap)}[/] " +
                $"{Markup.Escape(name.PadRight(nameWidth))} " +
                $"[grey]sub {hit.SubmeshIndex} tri {hit.TriangleIndex}[/] " +
                $"[grey]{Markup.Escape(DescribeFlags(hit))}[/]");
        }

        if (hits.Count > shown)
            AnsiConsole.MarkupLine($"   [grey]… {hits.Count - shown} further surface(s) not shown[/]");
    }

    private static string DescribeName(in ProbeHit hit)
    {
        if (!string.IsNullOrEmpty(hit.NodeName))
            return hit.NodeName;
        if (!string.IsNullOrEmpty(hit.MeshName))
            return hit.MeshName;
        return "(unnamed)";
    }

    private static string DescribeFlags(in ProbeHit hit)
    {
        var flags = new List<string>(5)
        {
            hit.AlphaMode == 1 ? "MASK" : "OPAQUE"
        };

        if (hit.HasTexture) flags.Add("textured");
        if (hit.IsDoubleSided) flags.Add("2-sided");
        if (hit.IsBackFacing) flags.Add("back-facing");

        // The exporters encode meaning in node names; surfacing it saves the reader
        // from having to know the conventions.
        var name = hit.NodeName ?? hit.MeshName ?? string.Empty;
        if (name.Contains("__overlay", StringComparison.Ordinal)) flags.Add("draw-order overlay");
        if (name.Contains("__ghost", StringComparison.Ordinal)) flags.Add("ghost");
        if (name.StartsWith("sky__", StringComparison.Ordinal)) flags.Add("sky layer");

        return string.Join(", ", flags);
    }

    private static string Distance(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatVector(float x, float y, float z) =>
        $"({Distance(x)}, {Distance(y)}, {Distance(z)})";
}
