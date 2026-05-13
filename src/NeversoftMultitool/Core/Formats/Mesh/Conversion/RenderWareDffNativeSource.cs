using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record RenderWareDffNativeSource(
    RwDffClump Clump,
    MeshNamedTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.RenderWareDff);
