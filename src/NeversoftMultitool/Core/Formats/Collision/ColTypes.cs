using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>Parsed collision file containing one or more collision objects.</summary>
public sealed class ColScene
{
    public required int Version { get; init; }
    public required ColObject[] Objects { get; init; }

    public int TotalVertices => Objects.Sum(o => o.Vertices.Length);
    public int TotalTriangles => Objects.Sum(o => o.Faces.Length);
}

/// <summary>A single collision object (sector) with its own geometry.</summary>
public sealed class ColObject
{
    public required uint Checksum { get; init; }
    public required ushort Flags { get; init; }
    public required Vector3 BBoxMin { get; init; }
    public required Vector3 BBoxMax { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required ColFace[] Faces { get; init; }
    public required byte[] Intensities { get; init; }
}

/// <summary>A collision triangle with flags and terrain type.</summary>
public readonly record struct ColFace(ushort Flags, ushort TerrainType, int V0, int V1, int V2);
