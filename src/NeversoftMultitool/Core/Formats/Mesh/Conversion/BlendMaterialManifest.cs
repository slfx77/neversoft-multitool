namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendMaterialManifest
{
    public required string Name { get; init; }
    public required float[] BaseColor { get; init; }
    public int? TextureIndex { get; init; }
    public required string AlphaMode { get; init; }
    public float AlphaCutoff { get; init; }
    public bool DoubleSided { get; init; }
    public bool Unlit { get; init; }
    public required List<Dictionary<string, object?>> NativeMetadata { get; init; }
}
