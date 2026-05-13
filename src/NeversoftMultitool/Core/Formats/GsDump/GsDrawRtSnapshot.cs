namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed record GsDrawRtSnapshot(
    long DrawIndex,
    uint Fbp,
    uint Fbw,
    uint Psm,
    uint Fbmsk,
    int Width,
    int Height,
    byte[] Rgba);
