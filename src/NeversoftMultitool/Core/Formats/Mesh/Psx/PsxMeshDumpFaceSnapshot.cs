namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpFaceSnapshot
{
    public required int FaceIndex { get; init; }
    public required int RawFaceIndex { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Length { get; init; }
    public required bool IsQuad { get; init; }
    public required bool IsTextured { get; init; }
    public required bool IsGouraud { get; init; }
    public required bool IsSemiTransparent { get; init; }
    public required uint NormalIndex { get; init; }
    public required uint TextureHash { get; init; }
    public required IReadOnlyList<uint> Indices { get; init; }
    public required IReadOnlyList<PsxMeshDumpTextureCoordinateSnapshot> TextureCoordinates { get; init; }
    public required IReadOnlyList<PsxMeshDumpVector3Snapshot> ResolvedWorldVertices { get; init; }
}
