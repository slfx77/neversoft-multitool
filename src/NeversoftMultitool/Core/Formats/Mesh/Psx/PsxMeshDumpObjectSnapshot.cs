namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpObjectSnapshot
{
    public required int ObjectIndex { get; init; }
    public required uint Flags { get; init; }
    public required ushort MeshIndex { get; init; }
    public required int ParentIndex { get; init; }
    public required int RawX { get; init; }
    public required int RawY { get; init; }
    public required int RawZ { get; init; }
    public required PsxMeshDumpVector3Snapshot Position { get; init; }
}
