namespace NeversoftMultitool.Core.Formats.GsDump;

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
