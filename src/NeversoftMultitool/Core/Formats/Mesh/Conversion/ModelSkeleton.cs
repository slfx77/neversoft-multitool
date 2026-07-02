namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelSkeleton
{
    public required string Name { get; init; }
    public List<ModelBone> Bones { get; } = [];
}
