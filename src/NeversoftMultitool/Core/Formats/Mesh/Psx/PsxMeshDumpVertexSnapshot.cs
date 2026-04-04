namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpVertexSnapshot
{
    public required int VertexIndex { get; init; }
    public required ushort Type { get; init; }
    public required short RawX { get; init; }
    public required short RawY { get; init; }
    public required short RawZ { get; init; }
    public required uint? AttachmentIndex { get; init; }
    public required uint? AttachmentTargetIndex { get; init; }
    public required PsxMeshDumpVector3Snapshot LocalPosition { get; init; }
    public required PsxMeshDumpVector3Snapshot WorldPosition { get; init; }
    public required bool UsedAttachment { get; init; }
    public required bool AttachmentResolved { get; init; }
    public required int SourceMeshIndex { get; init; }
    public required int SourceObjectIndex { get; init; }
    public required int SourceVertexIndex { get; init; }
}
