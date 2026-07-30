using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool;

/// <summary>
///     Sections the Display Settings visibility-group list so multi-object
///     level-area toggles, single-object props, and alternate-geometry
///     variants read as separate alphabetized subgroups.
/// </summary>
internal static class MeshConverterTabVisibilitySections
{
    private static readonly string[] SectionHeaders =
    [
        "Level areas / room streaming",
        "Individual objects / props",
        "Alternate geometry",
        "Other"
    ];

    /// <summary>
    ///     Partitions <paramref name="groups" /> into display sections, each
    ///     sorted alphabetically by label (ordinal, case-insensitive). Empty
    ///     sections are omitted; sources this build does not recognize land in
    ///     a trailing "Other" section.
    /// </summary>
    internal static IReadOnlyList<(string Header, IReadOnlyList<ModelVisibilityGroup> Groups)>
        Build(IReadOnlyList<ModelVisibilityGroup> groups)
    {
        return groups
            .GroupBy(GetSectionIndex)
            .OrderBy(static section => section.Key)
            .Select(static section => (
                SectionHeaders[section.Key],
                (IReadOnlyList<ModelVisibilityGroup>)section
                    .OrderBy(static group => group.Label, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
    }

    private static int GetSectionIndex(ModelVisibilityGroup group)
    {
        // The source enum may grow (other sessions add members); anything this
        // build does not recognize files under "Other" instead of throwing.
        return group.Source switch
        {
            ModelVisibilityGroupSource.TriggerRange =>
                IsSingleObjectTriggerRange(group.SourceReference) ? 1 : 0,
            ModelVisibilityGroupSource.AlternateGeometry => 2,
            _ => 3
        };
    }

    /// <summary>
    ///     A trigger range covering exactly one object suffix is an individual
    ///     prop toggle rather than a room-streaming area. The resolver formats
    ///     SourceReference as SetVisibilityByName("prefix", first, last), so
    ///     equal endpoints mean a single matched object; an unrecognized
    ///     reference conservatively stays in the level-area section.
    /// </summary>
    private static bool IsSingleObjectTriggerRange(string sourceReference)
    {
        if (!sourceReference.StartsWith("SetVisibilityByName(", StringComparison.Ordinal)
            || !sourceReference.EndsWith(')'))
        {
            return false;
        }

        var parts = sourceReference[..^1].Split(',');
        return parts.Length >= 3
               && int.TryParse(
                   parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
               && int.TryParse(
                   parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
               && first == last;
    }
}
