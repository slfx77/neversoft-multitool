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

    /// <summary>
    ///     Sink for end-of-frame per-(FBP, FBW, PSM) screen-space buffer dumps. Receives
    ///     (Fbp, Fbw, Psm, Width, Height, Rgba). The interpreter calls this once per
    ///     active surface after PCRTC composition.
    /// </summary>
    public Action<uint, uint, uint, int, int, byte[]>? DumpFbpBufferSink { get; init; }

    /// <summary>
    ///     Sink invoked for every kicked vertex (XYZ2/XYZF2 register write) with the raw
    ///     post-VU1 values before rasterisation. Ground-truth extraction for cross-referencing
    ///     GS-dump geometry against source mesh data.
    /// </summary>
    public Action<GsDrawVertexInfo>? DrawVertexSink { get; init; }
}

/// <summary>One kicked vertex as it arrived at the GS, prior to rasterisation.</summary>
internal readonly record struct GsDrawVertexInfo(
    int GifTagIndex,
    int Vsync,
    ulong Tex0,
    int PrimType,
    float X,
    float Y,
    float Z,
    float S,
    float T,
    float Q,
    bool NoKick);
