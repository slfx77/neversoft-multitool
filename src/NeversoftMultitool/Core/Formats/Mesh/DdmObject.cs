namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     A single mesh object within a DDM file.
/// </summary>
public sealed class DdmObject
{
    public required string Name { get; init; }
    public uint Checksum { get; init; }
    public uint Flags { get; init; }
    public float AnimSpeedX { get; init; }
    public float AnimSpeedY { get; init; }
    public float BBoxCenterX { get; init; }
    public float BBoxCenterY { get; init; }
    public float BBoxCenterZ { get; init; }
    public float BBoxExtentX { get; init; }
    public float BBoxExtentY { get; init; }
    public float BBoxExtentZ { get; init; }
    public required List<DdmMaterial> Materials { get; init; }
    public required List<DdmVertex> Vertices { get; init; }
    public required ushort[] Indices { get; init; }
    public required List<DdmSplit> Splits { get; init; }
}
