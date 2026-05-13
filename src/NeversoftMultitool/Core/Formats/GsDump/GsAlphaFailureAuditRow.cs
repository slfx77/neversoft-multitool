namespace NeversoftMultitool.Core.Formats.GsDump;

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
