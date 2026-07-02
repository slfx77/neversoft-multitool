namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelSkinBinding
{
    public required int SkeletonIndex { get; init; }
    public required ModelBoneInfluences[] Influences { get; init; }
}
