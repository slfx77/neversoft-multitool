using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Lit;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record DdmNativeSource(
    DdmFile File,
    string Name,
    Dictionary<string, byte[]>? DdxTextures,
    List<LitLight>? Lights)
    : ModelNativeSource(ModelSourceKind.Ddm);
