namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelAnimation
{
    public required string Name { get; init; }
    public List<ModelAnimationChannel> Channels { get; } = [];
}
