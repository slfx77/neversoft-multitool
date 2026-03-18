namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Mesh group containing one or more meshes.
///     Each group has a checksum identifier used for name resolution.
/// </summary>
public sealed class Ps2MeshGroup
{
    public uint Checksum { get; init; }
    public required List<Ps2Mesh> Meshes { get; init; }
}
