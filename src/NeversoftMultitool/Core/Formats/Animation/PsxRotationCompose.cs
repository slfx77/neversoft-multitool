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
    YZX
}
