namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Material flags from THUG source material.h.
/// </summary>
[Flags]
public enum Ps2MaterialFlags : uint
{
    UvWibble = 1 << 0,
    VcWibble = 1 << 1,
    Textured = 1 << 2,
    Environment = 1 << 3,
    Decal = 1 << 4,
    Smooth = 1 << 5,
    Transparent = 1 << 6,
    AnimatedTexture = 1 << 11,
    IgnoreVertexAlpha = 1 << 12
}
