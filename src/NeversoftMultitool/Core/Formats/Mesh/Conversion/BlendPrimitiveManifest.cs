namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendPrimitiveManifest
{
    public required string Name { get; init; }
    public int MaterialIndex { get; init; }
    public required string VertexBuffer { get; init; }
    public int VertexCount { get; init; }
    public required string IndexBuffer { get; init; }
    public int IndexCount { get; init; }
    public int TriangleCount { get; init; }
    public required List<Dictionary<string, object?>> NativeMetadata { get; init; }
}
