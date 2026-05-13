namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed record GsFramebufferSnapshot(
    string Key,
    uint Fbp,
    uint Fbw,
    uint Psm,
    uint Fbmsk,
    int Width,
    int Height,
    long NonBlackPixels,
    byte[] Rgba);
