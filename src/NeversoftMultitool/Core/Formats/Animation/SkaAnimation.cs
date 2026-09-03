namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parsed SKA animation file — per-bone rotation and translation keyframe tracks.
///     Format reference: THUG source Gfx/BonedAnim.cpp, Gfx/BonedAnimTypes.h.
/// </summary>
public sealed class SkaAnimation
{
    public required uint Version { get; init; }
    public required uint Flags { get; init; }
    public required float Duration { get; init; }
    public required SkaBoneTrack[] BoneTracks { get; init; }

    /// <summary>
    ///     Authored non-TRS events that follow the animation key streams.
    ///     THAW cutscene masters use these for camera FOV changes and scripts.
    /// </summary>
    public SkaCustomKey[] CustomKeys { get; init; } = [];

    /// <summary>
    ///     Embedded authoring skeleton and raw frame indices for a THUG
    ///     INTERMEDIATE stream. Null for ordinary runtime SKAs.
    /// </summary>
    internal SkaIntermediateMetadata? IntermediateMetadata { get; init; }

    /// <summary>
    ///     Project 8 / Proving Ground's 0x20-byte wrapper and section-addressed
    ///     THAW-family payload. The payload's first word is its 0x28/0x48 header
    ///     size, retained in <see cref="Version" /> for inspection.
    /// </summary>
    internal bool IsNextGenWrappedFormat { get; init; }

    public bool IsCompressedTime => (Flags & (1u << 26)) != 0;
    public bool IsPreRotatedRoot => (Flags & (1u << 25)) != 0;
    public bool UsesCompressTable => (Flags & (1u << 23)) != 0;
    public bool IsPlatformFormat => (Flags & (1u << 28)) != 0;
    public bool IsIntermediateFormat => (Flags & (1u << 30)) != 0;

    /// <summary>THAW-family container, including the later section-addressed revision.</summary>
    public bool IsThawFormat => Version == 0x28 || IsNextGenWrappedFormat;

    /// <summary>
    ///     THAW bits 14+17 (always paired in the corpus): translation keys are
    ///     deltas the engine adds onto the bone's neutral-pose translation
    ///     (verified in the THAW PS2 ELF key evaluator, which vadd-accumulates
    ///     the interpolated T onto the output slot). Camera/cutscene anims
    ///     leave these clear and store absolute translations.
    /// </summary>
    public bool IsAdditiveTranslation => (Flags & (1u << 14)) != 0 && (Flags & (1u << 17)) != 0;

    /// <summary>THAW bit 27: camera track data (hi-res float keys).</summary>
    public bool IsCameraData => (Flags & (1u << 27)) != 0;
}
