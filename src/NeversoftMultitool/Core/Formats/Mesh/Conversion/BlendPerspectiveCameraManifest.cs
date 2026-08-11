namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendPerspectiveCameraManifest
{
    public required string Name { get; init; }
    public required int SkeletonIndex { get; init; }
    public required int BoneIndex { get; init; }
    public float? AspectRatio { get; init; }
    public required float VerticalFieldOfViewRadians { get; init; }
    public required float ZNear { get; init; }
    public required float ZFar { get; init; }
}
