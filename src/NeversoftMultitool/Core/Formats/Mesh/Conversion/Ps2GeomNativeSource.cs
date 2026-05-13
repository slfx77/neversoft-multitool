using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record Ps2GeomNativeSource(
    Ps2GeomScene Scene,
    MeshChecksumTextureResolver? TextureProvider,
    Ps2Tex0ChecksumResolver? Tex0Resolver)
    : ModelNativeSource(ModelSourceKind.Ps2Geom);
