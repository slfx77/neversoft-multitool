using NeversoftMultitool.Core.Formats.Collision;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record NgcCollisionNativeSource(
    NgcColScene Collision,
    ParsedXbxScene RenderScene,
    string RenderCompanionName,
    string PositionPoolKind)
    : ModelNativeSource(ModelSourceKind.Collision);
