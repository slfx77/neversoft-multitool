namespace NeversoftMultitool.Core.Formats.GsDump;

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
