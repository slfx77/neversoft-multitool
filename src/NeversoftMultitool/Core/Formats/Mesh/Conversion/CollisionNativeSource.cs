using NeversoftMultitool.Core.Formats.Collision;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record CollisionNativeSource(ColScene Scene)
    : ModelNativeSource(ModelSourceKind.Collision);
