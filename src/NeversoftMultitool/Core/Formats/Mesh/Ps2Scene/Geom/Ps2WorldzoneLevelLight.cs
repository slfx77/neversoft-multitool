using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     One authored <c>Class = levellight</c> node from a THAW zone NodeArray —
///     the engine's per-level point lights (THUG <c>cfuncs.cpp:6417-6494</c>,
///     parsed AFTER the node array so light groups exist). Position, colour,
///     radii and the exclusion flags are authored data; <c>Brightness</c> is a
///     runtime placeholder in the shipped corpus (116/117 z_bh lights author 0
///     — the TOD scripts drive the live value), so consumers must treat it as
///     a seed, never as engine truth. <c>CreatedFromTod</c> carries the
///     TOD_{phase}{On|Off}_NN group gating the light, when authored.
/// </summary>
public sealed record Ps2WorldzoneLevelLight(
    uint NameChecksum,
    Vector3 Position,
    int ColorR,
    int ColorG,
    int ColorB,
    float Brightness,
    float InnerRadius,
    float OuterRadius,
    bool ExcludeLevel,
    bool ExcludeSkater,
    uint CreatedFromTod,
    uint CreatedFromVariable);
