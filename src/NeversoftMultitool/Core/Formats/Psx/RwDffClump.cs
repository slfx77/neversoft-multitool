namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Parsed RenderWare 3.x DFF (Clump) file.
///     Contains frame hierarchy, geometries, materials, and atomic links.
///     Used for THPS3 PS2 .SKN files (version 0x0310).
/// </summary>
public sealed class RwDffClump
{
    public required RwFrame[] Frames { get; init; }
    public required RwGeometry[] Geometries { get; init; }
    public required RwAtomic[] Atomics { get; init; }
}
