namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Maps a range of triangle strip indices to a material.
/// </summary>
public readonly record struct DdmSplit(
    ushort MaterialIndex,
    ushort IndexOffset,
    ushort IndexCount);
