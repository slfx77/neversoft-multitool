namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxMeshDumpAttachmentSnapshot
{
    public required uint AttachmentIndex { get; init; }
    public required int MeshIndex { get; init; }
    public required int ObjectIndex { get; init; }
    public required int VertexIndex { get; init; }
    public required PsxMeshDumpVector3Snapshot LocalPosition { get; init; }
    public required PsxMeshDumpVector3Snapshot WorldPosition { get; init; }
}
