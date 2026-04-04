using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

/// <summary>
///     Individual mesh with material reference and vertex data.
///     Triangle strips are encoded via ADC bits in vertex data — no separate index array.
///     Vertices are always per-mesh (all game versions).
/// </summary>
public sealed class Ps2Mesh
{
    public uint Checksum { get; init; }
    public uint MaterialChecksum { get; init; }
    public Ps2MeshFlags MeshFlags { get; init; }
    public Vector4 BoundingSphere { get; init; }
    public bool StartsOnOddOutputSlot { get; init; }
    public required Ps2Vertex[] Vertices { get; init; }
}
