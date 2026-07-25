namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed record PsxVisibilitySelection(
    IReadOnlyList<ModelVisibilityGroup> Groups,
    IReadOnlySet<int> HiddenObjectIndices)
{
    internal static PsxVisibilitySelection Empty { get; } = new([], new HashSet<int>());
}
