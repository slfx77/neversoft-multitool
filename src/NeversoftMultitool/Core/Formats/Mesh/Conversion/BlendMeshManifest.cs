namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendMeshManifest
{
    public required string Name { get; init; }
    public required List<BlendPrimitiveManifest> Primitives { get; init; }
    public required List<Dictionary<string, object?>> NativeMetadata { get; init; }
}
