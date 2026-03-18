namespace NeversoftMultitool.Core;

/// <summary>
///     Probes files to determine format support before processing.
///     Returns human-readable diagnostics for unsupported or partially supported formats.
/// </summary>
public static class FormatProbe
{
    public enum FormatSupport
    {
        Supported,
        Unsupported,
        PartiallySupported
    }

    public static FormatProbeResult ProbeTexture(string filePath) => FormatProbeTexture.Probe(filePath);

    public static FormatProbeResult ProbeMesh(string filePath) => FormatProbeMesh.Probe(filePath);

    public static FormatProbeResult ProbeArchive(string filePath) => FormatProbeArchive.Probe(filePath);

    public static FormatProbeResult ProbeAudio(string filePath) => FormatProbeAudio.Probe(filePath);

    public static FormatProbeResult ProbeVideo(string filePath) => FormatProbeVideo.Probe(filePath);

    /// <summary>
    ///     Probes a list of files, partitions into supported/unsupported, and returns counts.
    ///     The unsupported list contains (fileName, reason) pairs for warning output.
    /// </summary>
    public static (List<string> Supported, List<(string FileName, string Reason)> Unsupported)
        PartitionFiles(IEnumerable<string> files, Func<string, FormatProbeResult> probe)
    {
        var supported = new List<string>();
        var unsupported = new List<(string, string)>();

        foreach (var file in files)
        {
            var result = probe(file);
            if (result.Support == FormatSupport.Unsupported)
                unsupported.Add((Path.GetFileName(file), result.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(file);
        }

        return (supported, unsupported);
    }

    public record FormatProbeResult(
        FormatSupport Support,
        string FormatName,
        string? UnsupportedReason = null);
}
