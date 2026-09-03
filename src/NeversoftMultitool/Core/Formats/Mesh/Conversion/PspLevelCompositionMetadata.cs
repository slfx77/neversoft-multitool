namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Exact PSP scene ownership recovered from the same build's shipping
///     level manifest and loader scripts. File basenames keep diagnostics
///     portable; a null sky records an explicit authored no-sky structure.
/// </summary>
public sealed record PspLevelCompositionMetadata(
    string Game,
    string StructureName,
    string LevelSceneName,
    string? SkySceneName,
    string? OuterShellSceneName,
    bool IsNetworkVariant)
    : NativeRenderMetadata("psp_level_composition");
