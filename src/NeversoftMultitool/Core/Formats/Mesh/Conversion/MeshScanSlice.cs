namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>Which half of a scan a tab shows.</summary>
public enum MeshScanSlice
{
    /// <summary>Everything the scanner found.</summary>
    All,

    /// <summary>Level-scale content only (the Levels tab).</summary>
    Levels,

    /// <summary>Everything that is not level-scale (Meshes &amp; Characters).</summary>
    Models
}

/// <summary>
///     Splits one scan between the Levels tab and the Meshes &amp; Characters tab.
/// </summary>
/// <remarks>
///     The two slices are exact complements of <see cref="MeshLevelPolicy.IsLevelContent" />,
///     so nothing a scan finds can fall between the tabs or appear in both. Scanning
///     is expensive and archive-backed, so both tabs read one scan rather than
///     running their own.
/// </remarks>
public static class MeshScanSlicing
{
    /// <summary>Whether one scanned row belongs in <paramref name="slice" />.</summary>
    public static bool Includes(MeshScanSlice slice, in MeshLevelFacts facts) => slice switch
    {
        MeshScanSlice.Levels => MeshLevelPolicy.IsLevelContent(facts),
        MeshScanSlice.Models => !MeshLevelPolicy.IsLevelContent(facts),
        _ => true
    };
}
