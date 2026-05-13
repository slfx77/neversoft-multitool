namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     A discovered animation candidate. <see cref="MatchesSkeleton" /> reflects
///     whether the candidate's bone count is compatible with the active
///     character's skeleton (true if either count is unknown).
/// </summary>
internal sealed record AnimationProbe(
    AssetSource Source,
    string DisplayName,
    float DurationSec,
    int? BoneCount,
    bool MatchesSkeleton);
