using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Per-bone pose produced by <see cref="SkaPoseEvaluator.Evaluate" />.
///     <see cref="Rotation" /> and <see cref="Translation" /> are the bone's local
///     (parent-relative) transform at the sample time; <see cref="WorldMatrix" />
///     is the composed parent-chain matrix in model space.
/// </summary>
internal readonly struct SkaBonePose(Quaternion rotation, Vector3 translation, Matrix4x4 worldMatrix)
{
    public Quaternion Rotation { get; } = rotation;
    public Vector3 Translation { get; } = translation;
    public Matrix4x4 WorldMatrix { get; } = worldMatrix;
}
