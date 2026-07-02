namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendSkinManifest
{
    public required int SkeletonIndex { get; init; }
    public required string InfluenceBuffer { get; init; }
    public required int InfluenceCount { get; init; }
}
