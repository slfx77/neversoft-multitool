namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Quaternion composition order for converting the three Euler-angle channels
///     (Rx, Ry, Rz) into a single rotation. <c>YXZ</c> matches the engine's
///     <c>M3dMaths_RotMatrixYXZ</c> and is the default; the other variants are
///     diagnostic options to A/B test the composition convention.
/// </summary>
public enum PsxRotationCompose
{
    YXZ,
    ZXY,
    XYZ,
    ZYX,
    XZY,
    YZX,
}

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
