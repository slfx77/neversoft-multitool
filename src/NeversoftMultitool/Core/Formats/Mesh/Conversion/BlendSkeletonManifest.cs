namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendSkeletonManifest
{
    public required string Name { get; init; }
    public required List<BlendBoneManifest> Bones { get; init; }
}
