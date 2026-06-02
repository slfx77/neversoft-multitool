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
    public bool SaveRtOnStateTransition { get; init; }

    /// <summary>End-of-frame VRAM region dumps for Z-buffer / intermediate-FBP diagnostics.</summary>
    public IReadOnlyList<(uint Tbp, uint Fbw, uint Psm, int Width, int Height)>? DumpVramRegions { get; init; }

    /// <summary>
    ///     End-of-frame dump of each per-(FBP, FBW, PSM) screen-space buffer as a PNG.
    ///     Diagnostic for the per-FBP-buffer refactor (verifies HUD overlay FBP=11200
    ///     stays separate from main scene FBP=0).
    /// </summary>
    public bool DumpFbpBuffers { get; init; }
}
