using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     One caller-selected skeleton for an Xbox/PC/GameCube mesh entry. The
///     source is fully parsed before publication so preview and batch workers
///     never retain a file/archive handle.
/// </summary>
internal sealed record XbxSkeletonSelection(
    string DisplayLabel,
    Ps2Skeleton Skeleton);

/// <summary>
///     Content-and-format gate for the manual mesh skeleton picker. The parsed
///     sector flag proves that the scene carries joint records; the display
///     format keeps the control scoped to the three scene readers that retain
///     those records for <see cref="XbxGeometryWriter" />.
/// </summary>
internal static class XbxSkeletonEligibility
{
    public static bool Supports(string? format, bool hasSkinnedSectors) =>
        hasSkinnedSectors
        && (format?.StartsWith("Xbox (", StringComparison.Ordinal) == true
            || format?.StartsWith("PC (", StringComparison.Ordinal) == true
            || format?.StartsWith("GameCube (", StringComparison.Ordinal) == true);
}

/// <summary>
///     Pure projection for the WinUI mesh-skeleton controls. Keeping the state
///     rule outside WinUI makes supersession/visibility behavior testable on the
///     cross-platform test target.
/// </summary>
internal readonly record struct XbxSkeletonControlState(
    bool Visible,
    bool ChooseEnabled,
    bool ClearEnabled)
{
    public static XbxSkeletonControlState Create(
        bool eligibleEntrySelected,
        bool skeletonSelected,
        bool operationActive)
    {
        var idleEligible = eligibleEntrySelected && !operationActive;
        return new XbxSkeletonControlState(
            eligibleEntrySelected,
            idleEligible,
            idleEligible && skeletonSelected);
    }
}

/// <summary>
///     GUI conversion policy: the scale textbox belongs only to PS2
///     worldzones. It must not leak into another checked entry and silently
///     disable that entry's explicit Xbox/PC/GameCube skin.
/// </summary>
internal static class MeshGuiCoordinateScalePolicy
{
    public static float Resolve(bool isPakWorldzone, float requestedScale) =>
        isPakWorldzone ? requestedScale : 1f;
}

internal static class MeshGuiRenderPolicy
{
    public static bool IsSkeletonPreviewPending(
        bool hasPreviewEntry,
        bool isSameEntry,
        bool isSameSelection) =>
        hasPreviewEntry && isSameEntry && isSameSelection;

    public static bool RequiresEntryRebuild(
        bool isPakWorldzone,
        bool hasSupportedLevelObjectCompanion,
        bool supportsExplicitXbxSkeleton) =>
        isPakWorldzone
        || hasSupportedLevelObjectCompanion
        || supportsExplicitXbxSkeleton;
}
