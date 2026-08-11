namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     One 10-byte big-endian GameCube collision face record: flags, terrain
///     type, and three vertex indices into the file's global vertex numbering
///     (the positions themselves live in the render scene's vertex pool, not
///     in the collision file).
/// </summary>
public readonly record struct NgcColFace(
    ushort Flags,
    ushort TerrainType,
    ushort V0,
    ushort V1,
    ushort V2);
