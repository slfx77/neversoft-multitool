namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsDumpAuditReport
{
    public required string InputPath { get; init; }
    public string? ScreenshotPath { get; init; }
    public string? EmbeddedScreenshotPath { get; init; }
    public string? RawDirectRenderPath { get; init; }
    public string? RawRenderPath { get; init; }
    public string? DirectRenderPath { get; init; }
    public string? RenderPath { get; init; }
    public string? DirectDiffPath { get; init; }
    public string? DiffPath { get; init; }
    public string? MaterialDumpPath { get; init; }
    public string? TextureDumpDirectory { get; init; }
    public uint Crc { get; init; }
    public string Serial { get; init; } = "";
    public int StateVersion { get; init; }
    public int StateSize { get; init; }
    public int RegisterSnapshotSize { get; init; }
    public int ScreenshotWidth { get; init; }
    public int ScreenshotHeight { get; init; }
    public int PacketCount { get; init; }
    public Dictionary<string, long> PacketTypeCounts { get; init; } = [];
    public Dictionary<string, GsTransferStats> TransferStats { get; init; } = [];
    public GsGifAudit Gif { get; init; } = new();
    public GsRenderAudit Render { get; init; } = new();
    public GsPixelDiffStats? DirectPixelDiff { get; init; }
    public GsPixelDiffStats? PixelDiff { get; init; }
    public GsTextureCorrelationAudit? TextureCorrelation { get; init; }
}

internal sealed class GsTransferStats
{
    public long Packets { get; set; }
    public long Bytes { get; set; }
}

internal sealed class GsGifAudit
{
    public long ProcessedVu1Packets { get; set; }
    public long SkippedTransferPackets { get; set; }
    public long GifTagCount { get; set; }
    public long XyzWriteCount { get; set; }
    public int UniqueTex0Count { get; set; }
    public Dictionary<string, long> RegisterWrites { get; set; } = [];
    public Dictionary<string, long> XyzWritesByPrimitive { get; set; } = [];
    public List<GsTex0AuditRow> TopTex0ByXyz { get; set; } = [];
}

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

internal sealed class GsPresentedCircuitAudit
{
    public required int Circuit { get; init; }
    public required bool Enabled { get; init; }
    public string? Key { get; set; }
    public uint Fbp { get; set; }
    public uint Fbw { get; set; }
    public uint Psm { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dbx { get; set; }
    public int Dby { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Dw { get; set; }
    public int Dh { get; set; }
    public int Magh { get; set; }
    public int Magv { get; set; }
    public long NonBlackPixels { get; set; }
}

internal sealed class GsFramebufferTargetAudit
{
    public uint Fbp { get; set; }
    public uint Fbw { get; set; }
    public uint Psm { get; set; }
    public uint Fbmsk { get; set; }
    public long Draws { get; set; }
    public long PixelsWritten { get; set; }
    public GsPixelBounds? WriteBounds { get; set; }
    public Dictionary<string, long> Tex0Pixels { get; set; } = [];
}

internal sealed class GsFramebufferSnapshotAudit
{
    public required string Key { get; init; }
    public string? Path { get; set; }
    public uint Fbp { get; init; }
    public uint Fbw { get; init; }
    public uint Psm { get; init; }
    public uint Fbmsk { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long NonBlackPixels { get; init; }
}

internal sealed class GsImageTransferTargetAudit
{
    public uint Dbp { get; set; }
    public uint Dbw { get; set; }
    public uint Dpsm { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dsax { get; set; }
    public int Dsay { get; set; }
    public long Transfers { get; set; }
    public long Bytes { get; set; }
}

internal sealed class GsMissingTextureDrawAuditRow
{
    public required string Tex0 { get; init; }
    public uint Tbp { get; init; }
    public uint Tbw { get; init; }
    public uint Psm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Cbp { get; init; }
    public uint Cpsm { get; init; }
    public required string FramebufferKey { get; init; }
    public long Draws { get; init; }
    public GsPixelBounds? Bounds { get; init; }
}

internal sealed class GsAlphaFailureAuditRow
{
    public required string Tex0 { get; init; }
    public uint Tbp { get; init; }
    public uint Tbw { get; init; }
    public uint Psm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Cbp { get; init; }
    public uint Cpsm { get; init; }
    public required string FramebufferKey { get; init; }
    public required string FailMode { get; init; }
    public long Pixels { get; init; }
    public GsPixelBounds? Bounds { get; init; }
}

internal sealed class GsMaterialAuditRow
{
    public required string Key { get; init; }
    public required string Primitive { get; init; }
    public required string Prim { get; init; }
    public int ContextIndex { get; init; }
    public bool TextureEnabled { get; init; }
    public bool FogEnabled { get; init; }
    public bool AlphaBlendEnabled { get; init; }
    public bool FixedTextureCoordinates { get; init; }
    public long Draws { get; init; }
    public long MissingTextureDraws { get; init; }
    public long PixelsWritten { get; init; }
    public GsPixelBounds? Bounds { get; init; }
    public double MinR { get; init; }
    public double MaxR { get; init; }
    public double AvgR { get; init; }
    public double MinG { get; init; }
    public double MaxG { get; init; }
    public double AvgG { get; init; }
    public double MinB { get; init; }
    public double MaxB { get; init; }
    public double AvgB { get; init; }
    public double MinA { get; init; }
    public double MaxA { get; init; }
    public double AvgA { get; init; }
    public double MinU { get; init; }
    public double MaxU { get; init; }
    public double MinV { get; init; }
    public double MaxV { get; init; }
    public double MinQ { get; init; }
    public double MaxQ { get; init; }
    public required string Tex0 { get; init; }
    public uint TextureTbp { get; init; }
    public uint TextureTbw { get; init; }
    public uint TexturePsm { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public uint TextureTcc { get; init; }
    public uint TextureTfx { get; init; }
    public uint TextureCbp { get; init; }
    public uint TextureCpsm { get; init; }
    public uint TextureCsm { get; init; }
    public uint TextureCsa { get; init; }
    public uint TextureCld { get; init; }
    public required string Tex1 { get; init; }
    public uint Tex1Lcm { get; init; }
    public uint Tex1Mxl { get; init; }
    public uint Tex1Mmag { get; init; }
    public uint Tex1Mmin { get; init; }
    public required string Clamp { get; init; }
    public uint ClampWms { get; init; }
    public uint ClampWmt { get; init; }
    public uint ClampMinUOrMask { get; init; }
    public uint ClampMaxUOrFix { get; init; }
    public uint ClampMinVOrMask { get; init; }
    public uint ClampMaxVOrFix { get; init; }
    public required string Alpha { get; init; }
    public uint AlphaA { get; init; }
    public uint AlphaB { get; init; }
    public uint AlphaC { get; init; }
    public uint AlphaD { get; init; }
    public uint AlphaFix { get; init; }
    public required string Test { get; init; }
    public bool AlphaTestEnabled { get; init; }
    public uint AlphaTestMethod { get; init; }
    public uint AlphaRef { get; init; }
    public uint AlphaFailMode { get; init; }
    public bool DestinationAlphaTestEnabled { get; init; }
    public uint DestinationAlphaTestMode { get; init; }
    public bool DepthTestEnabled { get; init; }
    public uint DepthTestMethod { get; init; }
    public required string Texa { get; init; }
    public uint TexaTa0 { get; init; }
    public bool TexaAem { get; init; }
    public uint TexaTa1 { get; init; }
    public required string FogColor { get; init; }
    public required string FramebufferKey { get; init; }
    public uint FramebufferFbp { get; init; }
    public uint FramebufferFbw { get; init; }
    public uint FramebufferPsm { get; init; }
    public uint FramebufferMask { get; init; }
    public required string Zbuf { get; init; }
    public uint Zbp { get; init; }
    public uint Zpsm { get; init; }
    public bool Zmask { get; init; }
    public required string Scissor { get; init; }
    public int ScissorX0 { get; init; }
    public int ScissorY0 { get; init; }
    public int ScissorX1 { get; init; }
    public int ScissorY1 { get; init; }
    public bool DitherEnabled { get; init; }
    public bool FramebufferAlphaWriteEnabled { get; init; }
}

internal sealed class GsTextureDumpAuditRow
{
    public required string Key { get; init; }
    public string? Path { get; set; }
    public required string Source { get; init; }
    public string? SourceKey { get; init; }
    public required string Tex0 { get; init; }
    public required string Texa { get; init; }
    public uint ContentHash { get; init; }
    public uint? SourceChecksum { get; init; }
    public uint Tbp { get; init; }
    public uint Tbw { get; init; }
    public uint Psm { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public int RegionX { get; init; }
    public int RegionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Tcc { get; init; }
    public uint Tfx { get; init; }
    public uint Cbp { get; init; }
    public uint Cpsm { get; init; }
    public uint Csm { get; init; }
    public uint Csa { get; init; }
    public uint Cld { get; init; }
    public bool AllAlphaZero { get; init; }
}

internal sealed class GsPixelBounds
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long NonBlackPixels { get; init; }
}

internal sealed class GsTextureCorrelationAudit
{
    public int UniqueRuntimeTex0 { get; set; }
    public int ResolvedTex0 { get; set; }
    public List<GsTex0CorrelationRow> TopRuntimeTex0 { get; set; } = [];
}

internal sealed class GsTex0AuditRow
{
    public required string Tex0 { get; init; }
    public uint Tbp { get; init; }
    public uint Tbw { get; init; }
    public uint Psm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Cbp { get; init; }
    public uint Cpsm { get; init; }
    public long XyzWrites { get; init; }
}

internal sealed class GsTex0CorrelationRow
{
    public required string Tex0 { get; init; }
    public long XyzWrites { get; init; }
    public uint? TextureChecksum { get; init; }
    public string? ResolutionMode { get; init; }
}

internal sealed class GsPixelDiffStats
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteError { get; init; }
    public double RootMeanSquareError { get; init; }
    public int MaxChannelDifference { get; init; }
    public GsPixelBounds? RenderBounds { get; init; }
    public GsPixelBounds? ReferenceBounds { get; init; }
    public List<GsMismatchRegion> TopMismatchRegions { get; init; } = [];
}

internal sealed class GsMismatchRegion
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteError { get; init; }
}
