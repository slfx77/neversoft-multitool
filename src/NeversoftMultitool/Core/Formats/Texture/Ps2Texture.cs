namespace NeversoftMultitool.Core.Formats.Texture;

public sealed record Ps2Texture(
    uint Checksum,
    int Width,
    int Height,
    uint Psm,
    uint Cpsm,
    byte[]? Pixels,
    string? Name = null);
