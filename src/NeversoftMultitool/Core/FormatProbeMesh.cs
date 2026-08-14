using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Core;

/// <summary>
///     Adapts <see cref="MeshTypeDetector" /> to the probe result shape the CLI
///     pre-filters and the GUI pre-scan dialog consume. All extension and content
///     decisions live in the detector — this file only maps a route to a verdict.
/// </summary>
internal static class FormatProbeMesh
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var route = MeshTypeDetector.Detect(filePath);

        if (route.IsSupported)
        {
            if (!File.Exists(filePath))
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    route.DisplayFormat ?? "Mesh",
                    "File not found");
            }

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                route.DisplayFormat ?? "Mesh");
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            route.DisplayFormat ?? "Unknown",
            route.UnsupportedReason ?? "Unrecognized mesh format");
    }
}
