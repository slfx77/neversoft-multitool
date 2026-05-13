namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelDocument
{
    public required string Name { get; init; }
    public ModelSourceKind SourceKind { get; init; } = ModelSourceKind.Generic;
    public List<ModelScene> Scenes { get; } = [];
    public List<ModelNode> Nodes { get; } = [];
    public List<ModelMesh> Meshes { get; } = [];
    public List<RenderMaterial> Materials { get; } = [];
    public List<ModelTexture> Textures { get; } = [];
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
    public ModelNativeSource? NativeSource { get; init; }
    public int TriangleCount { get; set; }

    public static ModelDocument CreateNative(
        string name,
        ModelSourceKind sourceKind,
        ModelNativeSource nativeSource,
        int triangleCount = 0)
    {
        return new ModelDocument
        {
            Name = name,
            SourceKind = sourceKind,
            NativeSource = nativeSource,
            TriangleCount = triangleCount
        };
    }
}
