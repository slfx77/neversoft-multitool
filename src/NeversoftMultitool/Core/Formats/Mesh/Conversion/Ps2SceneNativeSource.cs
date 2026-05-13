using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record Ps2SceneNativeSource(
    Ps2Scene.Scene.Ps2Scene Scene,
    Ps2Skeleton? Skeleton,
    MeshChecksumTextureResolver? TextureProvider)
    : ModelNativeSource(ModelSourceKind.Ps2Scene);
