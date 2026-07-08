using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed class PsxAttachmentVertex
{
    public required uint AttachmentIndex { get; init; }
    public required int MeshIndex { get; init; }
    public required int VertexIndex { get; init; }
    public required Vector3 LocalPosition { get; init; }

    // Settable: collected before the meshes are parsed, so flat stitched
    // supers (whose part binding is positional — see
    // PsxMeshSemantics.UsesCharacterObjectOrder) re-derive these once the
    // full file is known.
    public required int ObjectIndex { get; set; }
    public required Vector3 WorldPosition { get; set; }
}
