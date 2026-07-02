namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     One per-bone keyframe track inside a <see cref="ModelAnimation" />.
///     <para>
///         <see cref="Values" /> packs floats per key according to <see cref="Property" />:
///         Translation/Scale = 3 floats per key (X, Y, Z); Rotation = 4 floats per key
///         (X, Y, Z, W). <see cref="Times" /> is in seconds and parallel to keys.
///     </para>
/// </summary>
public sealed class ModelAnimationChannel
{
    public required int SkeletonIndex { get; init; }
    public required int BoneIndex { get; init; }
    public required ModelAnimationProperty Property { get; init; }
    public required float[] Times { get; init; }
    public required float[] Values { get; init; }
    public ModelAnimationInterpolation Interpolation { get; init; } = ModelAnimationInterpolation.Linear;

    public int KeyCount => Times.Length;

    public int ValueStride => Property == ModelAnimationProperty.Rotation ? 4 : 3;
}
