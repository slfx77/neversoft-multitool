namespace NeversoftMultitool.Core.Formats.GsDump;

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
