namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsDumpAuditOptions
{
    public string? PngPath { get; init; }
    public string? TexturePath { get; init; }
    public bool JsonOnly { get; init; }
    public bool Verbose { get; init; }
    public int? ProbeX { get; init; }
    public int? ProbeY { get; init; }
    public uint? ProbeFbp { get; init; }
    public string? ProbeOutputPath { get; init; }
    public int? MaxVsync { get; init; }
    public string? SaveRtDir { get; init; }
    public int SaveRtStart { get; init; }
    public int? SaveRtCount { get; init; }
    public uint? SaveRtFbp { get; init; }
}
