using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelSkeleton
{
    public required string Name { get; init; }
    public Matrix4x4 RootTransform { get; init; } = Matrix4x4.Identity;
    public List<ModelBone> Bones { get; } = [];
}
