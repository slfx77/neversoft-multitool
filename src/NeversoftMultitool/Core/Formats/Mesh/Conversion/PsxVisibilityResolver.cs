using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Combines the two PSX mechanisms which select geometry at runtime:
///     level TRG visibility ranges and conservative character leaf variants.
/// </summary>
internal static class PsxVisibilityResolver
{
    internal static PsxVisibilitySelection Resolve(
        AssetSource source,
        string fileName,
        PsxMeshFile file,
        IReadOnlyDictionary<string, bool>? overrides)
    {
        var definitions =
            PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(file)
                ? BuildAlternateGeometryGroups(file, fileName)
                : PsxTriggerVisibilityResolver.FindVisibilityGroups(source, fileName, file);

        if (definitions.Count == 0)
            return PsxVisibilitySelection.Empty;

        var descriptors = new List<ModelVisibilityGroup>(definitions.Count);
        var hiddenObjects = new HashSet<int>();
        var enabledExclusiveSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var enabled = overrides?.TryGetValue(definition.Id, out var selected) == true
                ? selected
                : definition.DefaultEnabled;

            // A malformed or non-UI caller can submit more than one true
            // override for a multi-variant placement. Keep the first authored
            // choice deterministically and normalize every sibling to false.
            if (enabled
                && definition.ExclusiveSetId is { } exclusiveSetId
                && !enabledExclusiveSets.Add(exclusiveSetId))
            {
                enabled = false;
            }

            descriptors.Add(new ModelVisibilityGroup
            {
                Id = definition.Id,
                Label = definition.Label,
                DefaultEnabled = definition.DefaultEnabled,
                IsEnabled = enabled,
                Source = definition.Source,
                SourceReference = definition.SourceReference,
                ExclusiveSetId = definition.ExclusiveSetId
            });

            // A trigger range has no disabled-state geometry, whereas an
            // alternate leaf swaps the default and alternate objects.
            hiddenObjects.UnionWith(enabled
                ? definition.VisibleWhenDisabledObjectIndices
                : definition.VisibleWhenEnabledObjectIndices);
        }

        return new PsxVisibilitySelection(descriptors, hiddenObjects);
    }

    private static List<PsxVisibilityGroupDefinition> BuildAlternateGeometryGroups(
        PsxMeshFile file,
        string fileName)
    {
        var alternateLeaves = PsxMeshSemantics.FindAlternateLeafObjectGroups(file);
        if (alternateLeaves.Count == 0)
            return [];

        var definitions = new List<PsxVisibilityGroupDefinition>(alternateLeaves.Count);
        var assetHash = QbKey.QbKey.Hash(
            Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant());
        var alternateSetSizes = alternateLeaves
            .GroupBy(static group => (
                group.ParentObjectIndex,
                group.DefaultObjectIndex))
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var group in alternateLeaves)
        {
            var defaultMeshIndex = PsxMeshSemantics.GetCharacterMeshIndex(
                file, group.DefaultObjectIndex);
            var alternateMeshIndex = PsxMeshSemantics.GetCharacterMeshIndex(
                file, group.AlternateObjectIndex);
            var defaultName = PsxGeometryHelpers.ResolvePsxMeshName(file, defaultMeshIndex);
            var alternateName = PsxGeometryHelpers.ResolvePsxMeshName(file, alternateMeshIndex);
            var id = $"psx.alt.{assetHash:X8}.{group.ParentObjectIndex:D3}." +
                     $"{group.DefaultObjectIndex:D3}.{group.AlternateObjectIndex:D3}";
            var exclusiveSetId = alternateSetSizes[(
                group.ParentObjectIndex,
                group.DefaultObjectIndex)] > 1
                ? $"psx.altset.{assetHash:X8}.{group.ParentObjectIndex:D3}." +
                  $"{group.DefaultObjectIndex:D3}"
                : null;

            definitions.Add(new PsxVisibilityGroupDefinition(
                id,
                $"{alternateName} instead of {defaultName}",
                false,
                ModelVisibilityGroupSource.AlternateGeometry,
                $"PSX objects {group.DefaultObjectIndex} / {group.AlternateObjectIndex}, " +
                $"parent {group.ParentObjectIndex}",
                exclusiveSetId,
                new HashSet<int> { group.AlternateObjectIndex },
                new HashSet<int> { group.DefaultObjectIndex }));
        }

        return definitions;
    }
}
