using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelNode
{
    public required string Name { get; init; }
    public int? MeshIndex { get; init; }
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;
    public List<int> ChildNodeIndices { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}
