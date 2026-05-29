namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsRenderAudit
{
    public int Width { get; set; }
    public int Height { get; set; }
    public float CoordinateScaleX { get; set; } = 1f;
    public float CoordinateScaleY { get; set; } = 1f;
    public long DrawsSeen { get; set; }
    public long PointsDrawn { get; set; }
    public long TrianglesDrawn { get; set; }
    public long SpritesDrawn { get; set; }
    public long PixelsTouched { get; set; }
    public long DepthRejectedPixels { get; set; }
    public long DepthVramWrites { get; set; }
    public long DepthVramWritesSkippedFbw0 { get; set; }
    public long DepthVramWritesSkippedPsm { get; set; }
    public long AlphaFailedPixels { get; set; }
    public long AlphaFailFramebufferOnlyPixels { get; set; }
    public long AlphaFailZBufferOnlyPixels { get; set; }
    public long AlphaFailRgbOnlyPixels { get; set; }
    public long FixedTexturePixels { get; set; }
    public long PerspectiveTexturePixels { get; set; }
    public long TextureCacheHits { get; set; }
    public long TextureDecodeMisses { get; set; }
    public long ImageTransfersStarted { get; set; }
    public long ImageTransfersCompleted { get; set; }
    public long ImageTransferBytes { get; set; }
    public bool InitialGsMemorySeeded { get; set; }
    public bool PresentedFramebuffer { get; set; }

    // The next seven fields mirror the visible front layer (typically circuit 1) for
    // back-compat with prior audit consumers. The authoritative per-circuit detail is
    // in PresentedCircuits below; PCRTC composition uses both circuits when enabled.
    public string? PresentedFramebufferKey { get; set; }
    public uint PresentedFramebufferFbp { get; set; }
    public uint PresentedFramebufferFbw { get; set; }
    public int PresentedFramebufferWidth { get; set; }
    public int PresentedFramebufferHeight { get; set; }
    public uint PresentedFramebufferPsm { get; set; }
    public long PresentedFramebufferNonBlackPixels { get; set; }
    public List<GsPresentedCircuitAudit> PresentedCircuits { get; set; } = [];
    public ulong PmodeRaw { get; set; }
    public bool PmodeEn1 { get; set; }
    public bool PmodeEn2 { get; set; }
    public bool PmodeMmod { get; set; }
    public bool PmodeAmod { get; set; }
    public bool PmodeSlbg { get; set; }
    public uint PmodeAlp { get; set; }
    public uint BgColor { get; set; }
    public bool PresentationFitApplied { get; set; }
    public string? PresentationFitReason { get; set; }
    public GsPixelBounds? PresentationSourceBounds { get; set; }
    public GsPixelBounds? PresentationReferenceBounds { get; set; }
    public GsPixelBounds? RawDirectNonBlackBounds { get; set; }
    public GsPixelBounds? RawPresentedNonBlackBounds { get; set; }
    public GsPixelBounds? DirectNonBlackBounds { get; set; }
    public GsPixelBounds? PresentedNonBlackBounds { get; set; }
    public Dictionary<string, GsFramebufferTargetAudit> FramebufferTargets { get; set; } = [];
    public List<GsFramebufferSnapshotAudit> FramebufferSnapshots { get; set; } = [];
    public Dictionary<string, GsImageTransferTargetAudit> ImageTransferTargets { get; set; } = [];
    public List<GsMissingTextureDrawAuditRow> MissingTextureDraws { get; set; } = [];
    public List<GsAlphaFailureAuditRow> AlphaFailureDraws { get; set; } = [];
    public List<GsMaterialAuditRow> Materials { get; set; } = [];
    public List<GsTextureDumpAuditRow> TextureDumps { get; set; } = [];
    public Dictionary<string, long> UnsupportedStates { get; set; } = [];
    public Dictionary<string, long> Approximations { get; set; } = [];
}
