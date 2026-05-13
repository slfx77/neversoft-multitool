using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record PsxNativeSource(
    PsxMeshFile File,
    MeshChecksumTextureResolver TextureProvider,
    PshFile? PshFile)
    : ModelNativeSource(ModelSourceKind.Psx);
