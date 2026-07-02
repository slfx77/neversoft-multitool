namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendAnimationChannelManifest
{
    public required int SkeletonIndex { get; init; }
    public required int BoneIndex { get; init; }
    public required string Property { get; init; }
    public required string TimesBuffer { get; init; }
    public required string ValuesBuffer { get; init; }
    public required int KeyCount { get; init; }
    public required int ValueStride { get; init; }
    public required string Interpolation { get; init; }
}
