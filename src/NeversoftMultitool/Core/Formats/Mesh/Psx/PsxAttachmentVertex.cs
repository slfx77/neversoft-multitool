using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxAttachmentVertex
{
    public required uint AttachmentIndex { get; init; }
    public required int MeshIndex { get; init; }
    public required int ObjectIndex { get; init; }
    public required int VertexIndex { get; init; }
    public required Vector3 LocalPosition { get; init; }
    public required Vector3 WorldPosition { get; init; }
}
