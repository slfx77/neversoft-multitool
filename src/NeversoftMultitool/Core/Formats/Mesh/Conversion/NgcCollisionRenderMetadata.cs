namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>Records a structurally proven GameCube external position-pool join.</summary>
public sealed record NgcCollisionRenderMetadata(
    string CompanionName,
    string PositionPoolKind,
    int ObjectCount,
    int TriangleCount)
    : NativeRenderMetadata("ngc-collision-binding");
