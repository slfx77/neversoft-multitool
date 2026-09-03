namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Records a THPS3 PS2 main/sky join selected from the shipping
///     <c>SKATE3/Scripts/levels.qb</c> single-player load script. Asset paths
///     are retained in engine form so exported diagnostics are host-independent.
/// </summary>
public sealed record Thps3Ps2LevelCompositionMetadata(
    string DisplayName,
    string LoadScriptName,
    string LevelAssetPath,
    string? SkyAssetPath,
    uint? BackgroundColor)
    : NativeRenderMetadata("thps3_ps2_level_composition");
