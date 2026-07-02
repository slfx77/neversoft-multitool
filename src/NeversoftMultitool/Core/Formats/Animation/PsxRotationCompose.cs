namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Quaternion composition order for converting the three Euler-angle channels
///     (Rx, Ry, Rz) into a single rotation. <c>YXZ</c> is the default exporter
///     convention; the other variants are diagnostic options to A/B test row,
///     column, and axis-order assumptions.
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
