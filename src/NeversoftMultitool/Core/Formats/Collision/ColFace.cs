namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>A collision triangle with flags and terrain type.</summary>
public readonly record struct ColFace(ushort Flags, ushort TerrainType, int V0, int V1, int V2);
