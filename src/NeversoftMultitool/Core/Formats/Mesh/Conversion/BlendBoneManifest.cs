namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendBoneManifest
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public required float[] LocalTransform { get; init; }
    public required float[] InverseBindMatrix { get; init; }
    public uint NativeChecksum { get; init; }
}
