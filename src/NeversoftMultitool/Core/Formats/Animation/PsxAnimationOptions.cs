namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Options that control how PSX animation tracks are emitted to glTF.
///     Used to A/B test rotation/translation conventions while the format is
///     being calibrated.
/// </summary>
public sealed record PsxAnimationOptions(
    bool SkipRotation = false,
    bool SkipTranslation = false,
    PsxRotationCompose RotationCompose = PsxRotationCompose.YXZ,
    float Fps = 30f);
