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
    bool MatchesSkeleton)
{
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
