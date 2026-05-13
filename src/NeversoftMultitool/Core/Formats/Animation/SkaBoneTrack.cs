namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Per-bone rotation and translation keyframe track.</summary>
internal sealed class SkaBoneTrack
{
    public required int BoneIndex { get; init; }
    public required SkaRotationKey[] RotationKeys { get; init; }
    public required SkaTranslationKey[] TranslationKeys { get; init; }
}
