namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelMesh
{
    public required string Name { get; init; }
    public List<ModelPrimitive> Primitives { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}
