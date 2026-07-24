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
    bool MatchesSkeleton,
    int? FrameCount = null)
{
    /// <summary>
    ///     True for static single-pose "animations" (one keyframe, e.g. the
    ///     hundreds of pose slots in Spider-Man's PSX banks). SKA headers don't
    ///     expose a frame count, so a zero duration is the equivalent signal.
    /// </summary>
    public bool IsSinglePose => FrameCount is <= 1 || DurationSec <= 0f;

    /// <summary>
    ///     Non-empty label used by animation lists. Some formats do not store
    ///     clip names, so fall back to the source's synthetic entry name.
    /// </summary>
    public string ResolvedDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
                return DisplayName;

            var sourceName = Path.GetFileName(Source.EntryName);
            return string.IsNullOrWhiteSpace(sourceName) ? "Unnamed animation" : sourceName;
        }
    }

    /// <summary>Formatted duration used by animation lists.</summary>
    public string DurationDisplay => $"{DurationSec:0.00} s";
}
