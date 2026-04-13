using System.Numerics;

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

/// <summary>Per-bone rotation and translation keyframe track.</summary>
internal sealed class SkaBoneTrack
{
    public required int BoneIndex { get; init; }
    public required SkaRotationKey[] RotationKeys { get; init; }
    public required SkaTranslationKey[] TranslationKeys { get; init; }
}

/// <summary>Decompressed rotation keyframe (unit quaternion + timestamp).</summary>
internal readonly struct SkaRotationKey(float time, Quaternion rotation)
{
    public float Time { get; } = time;
    public Quaternion Rotation { get; } = rotation;
}

/// <summary>Decompressed translation keyframe (position + timestamp).</summary>
internal readonly struct SkaTranslationKey(float time, Vector3 translation)
{
    public float Time { get; } = time;
    public Vector3 Translation { get; } = translation;
}
