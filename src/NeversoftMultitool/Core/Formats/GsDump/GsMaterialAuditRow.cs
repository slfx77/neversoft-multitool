namespace NeversoftMultitool.Core.Formats.GsDump;

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
