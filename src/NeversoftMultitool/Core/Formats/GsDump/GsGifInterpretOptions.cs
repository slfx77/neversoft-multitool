namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsGifInterpretOptions
{
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 448;
    public float CoordinateScaleX { get; init; } = 1f;
    public float CoordinateScaleY { get; init; } = 1f;
    public Func<ulong, GsResolvedTexture?>? TextureResolver { get; init; }
    public Func<GsRuntimeTextureDump, string?>? TextureDumpSink { get; init; }
    public int? ProbeX { get; init; }
    public int? ProbeY { get; init; }
    public uint? ProbeFbp { get; init; }
    public Action<GsPixelProbeInfo>? PixelProbe { get; init; }
    public int? MaxVsync { get; init; }
    public int SaveRtStart { get; init; }
    public int? SaveRtCount { get; init; }
    public uint? SaveRtFbp { get; init; }
    public bool SaveRtOnStateTransition { get; init; }
    public Action<GsDrawRtSnapshot>? SaveRtSink { get; init; }

    /// <summary>End-of-frame VRAM region dumps. Each tuple = (Tbp, Fbw, Psm, Width, Height).</summary>
    public IReadOnlyList<(uint Tbp, uint Fbw, uint Psm, int Width, int Height)>? DumpVramRegions { get; init; }

    /// <summary>Sink for end-of-frame VRAM region dumps. Receives (Tbp, Fbw, Psm, Width, Height, Rgba).</summary>
    public Action<uint, uint, uint, int, int, byte[]>? DumpVramRegionSink { get; init; }
}
