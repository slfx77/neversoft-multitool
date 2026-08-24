using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record NdsGeometryNativeSource(NdsGeometryFile File, IReadOnlyList<NdsGeometryGroup> Groups)
    : ModelNativeSource(ModelSourceKind.Generic);
