namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Records the exact scene ownership read from THPS4 Windows'
///     <c>data/scripts/Levels.qb</c>. Basenames are retained instead of host
///     paths so exported diagnostics remain portable.
/// </summary>
public sealed record Thps4PcLevelCompositionMetadata(
    string StructureName,
    string LevelSceneName,
    string SkySceneName,
    string? OuterShellSceneName)
    : NativeRenderMetadata("thps4_pc_level_composition");
