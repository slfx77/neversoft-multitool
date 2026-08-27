namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     One primitive morph target: a buffer of <c>VertexCount</c> × 3 float32
///     position deltas parallel to the primitive's vertex buffer, which the
///     importer adds to the base coordinates to build a Blender shape key.
/// </summary>
internal sealed class BlendMorphTargetManifest
{
    public required string Name { get; init; }
    public required string PositionDeltaBuffer { get; init; }
    public required int VertexCount { get; init; }
}
