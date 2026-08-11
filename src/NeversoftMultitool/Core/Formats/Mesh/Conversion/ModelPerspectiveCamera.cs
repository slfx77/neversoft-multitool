namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Static perspective-camera projection bound to a bone in a model skeleton.
///     The target bone supplies the camera's transform and animation; projection
///     animation is intentionally outside this contract.
/// </summary>
public sealed class ModelPerspectiveCamera
{
    public required string Name { get; init; }
    public required int SkeletonIndex { get; init; }
    public required int BoneIndex { get; init; }
    public float? AspectRatio { get; init; }
    public required float VerticalFieldOfViewRadians { get; init; }
    public required float ZNear { get; init; }
    public required float ZFar { get; init; }
}
