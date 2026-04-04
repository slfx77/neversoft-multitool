namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpMeshSnapshot
{
    public required int MeshIndex { get; init; }
    public required int ObjectIndex { get; init; }
    public required int VertexCount { get; init; }
    public required int NormalCount { get; init; }
    public required int FaceCount { get; init; }
    public required int RawFaceCount { get; init; }
    public required int StitchSourceCount { get; init; }
    public required int StitchedReferenceCount { get; init; }
    public required int StitchFailureCount { get; init; }
    public required short LodDepth { get; init; }
    public required ushort LodNextMeshIndex { get; init; }
    public required bool HasPerVertexNormals { get; init; }
    public required IReadOnlyList<PsxMeshDumpVertexSnapshot> Vertices { get; init; }
    public required IReadOnlyList<PsxMeshDumpFaceReadSnapshot> FaceReads { get; init; }
    public required IReadOnlyList<PsxMeshDumpFaceSnapshot> Faces { get; init; }
}
