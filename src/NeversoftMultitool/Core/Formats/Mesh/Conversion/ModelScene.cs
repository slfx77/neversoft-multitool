namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class ModelScene
{
    public required string Name { get; init; }
    public List<int> RootNodeIndices { get; } = [];
}
