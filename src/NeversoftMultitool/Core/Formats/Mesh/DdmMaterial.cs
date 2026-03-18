namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Material with texture reference and rendering properties.
/// </summary>
public sealed class DdmMaterial
{
    public required string Name { get; init; }
    public required string TextureName { get; init; }
    public uint DrawOrder { get; init; }
    public byte DiffuseR { get; init; }
    public byte DiffuseG { get; init; }
    public byte DiffuseB { get; init; }
    public byte DiffuseA { get; init; }
    public float Emissive { get; init; }
    public float SpecularLevel { get; init; }
    public float Glossiness { get; init; }
    public uint BlendMode { get; init; }
}
