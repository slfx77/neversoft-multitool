namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendNodeManifest
{
    public required string Name { get; init; }
    public int? MeshIndex { get; init; }
    public required float[] Transform { get; init; }
    public required List<int> ChildNodeIndices { get; init; }
    public required List<Dictionary<string, object?>> NativeMetadata { get; init; }
}
