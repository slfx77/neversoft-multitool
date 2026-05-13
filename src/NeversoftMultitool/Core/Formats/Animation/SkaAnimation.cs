namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parsed SKA animation file — per-bone rotation and translation keyframe tracks.
///     Format reference: THUG source Gfx/BonedAnim.cpp, Gfx/BonedAnimTypes.h.
/// </summary>
internal sealed class SkaAnimation
{
    public required uint Version { get; init; }
    public required uint Flags { get; init; }
    public required float Duration { get; init; }
    public required SkaBoneTrack[] BoneTracks { get; init; }

    public bool IsCompressedTime => (Flags & (1u << 26)) != 0;
    public bool IsPreRotatedRoot => (Flags & (1u << 25)) != 0;
    public bool UsesCompressTable => (Flags & (1u << 23)) != 0;
    public bool IsPlatformFormat => (Flags & (1u << 28)) != 0;
}
