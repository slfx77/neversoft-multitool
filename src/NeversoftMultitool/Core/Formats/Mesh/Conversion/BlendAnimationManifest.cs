namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed class BlendAnimationManifest
{
    public required string Name { get; init; }
    public required List<BlendAnimationChannelManifest> Channels { get; init; }
}
