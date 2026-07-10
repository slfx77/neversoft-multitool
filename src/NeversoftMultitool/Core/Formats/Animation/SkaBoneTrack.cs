namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Per-bone rotation and translation keyframe track.</summary>
public sealed class SkaBoneTrack
{
    public required int BoneIndex { get; init; }
    public required SkaRotationKey[] RotationKeys { get; init; }
    public required SkaTranslationKey[] TranslationKeys { get; init; }

    /// <summary>
    ///     QbKey of the bone/node this track drives, when the file names its
    ///     targets (THAW OBJECTANIMDATA cutscene/camera anims). Null for
    ///     skeleton anims, whose tracks bind by index.
    /// </summary>
    public uint? BoneNameChecksum { get; init; }
}
