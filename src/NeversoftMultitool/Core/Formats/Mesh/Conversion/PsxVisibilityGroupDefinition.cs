namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal sealed record PsxVisibilityGroupDefinition(
    string Id,
    string Label,
    bool DefaultEnabled,
    ModelVisibilityGroupSource Source,
    string SourceReference,
    string? ExclusiveSetId,
    IReadOnlySet<int> VisibleWhenEnabledObjectIndices,
    IReadOnlySet<int> VisibleWhenDisabledObjectIndices);
