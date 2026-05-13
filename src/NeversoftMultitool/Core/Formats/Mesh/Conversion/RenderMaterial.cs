using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class RenderMaterial
{
    public required string Name { get; init; }
    public Vector4 BaseColor { get; set; } = Vector4.One;
    public int? TextureIndex { get; set; }
    public ModelAlphaMode AlphaMode { get; set; } = ModelAlphaMode.Opaque;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; } = true;
    public bool Unlit { get; set; } = true;
    public List<NativeRenderMetadata> NativeMetadata { get; } = [];
}
