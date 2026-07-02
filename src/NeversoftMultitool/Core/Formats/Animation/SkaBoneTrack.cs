namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Per-bone rotation and translation keyframe track.</summary>
public sealed class SkaBoneTrack
{
    public required int BoneIndex { get; init; }
    public required SkaRotationKey[] RotationKeys { get; init; }
    public required SkaTranslationKey[] TranslationKeys { get; init; }
}
