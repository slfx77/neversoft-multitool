using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record RenderWareBspNativeSource(
    RwBspWorld World,
    MeshNamedTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.RenderWareBsp);
