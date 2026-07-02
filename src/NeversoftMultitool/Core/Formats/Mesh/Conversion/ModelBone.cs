using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelBone
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public Matrix4x4 LocalTransform { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 InverseBindMatrix { get; init; } = Matrix4x4.Identity;
    public uint NativeChecksum { get; init; }
}
