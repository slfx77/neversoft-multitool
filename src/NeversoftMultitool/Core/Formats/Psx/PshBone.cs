namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     A single bone/part entry from a PSH header file.
/// </summary>
public sealed class PshBone
{
    /// <summary>Bone name extracted from the #define after PART_ prefix (lowercased).</summary>
    public required string Name { get; init; }

    /// <summary>Zero-based index from the #define value. Corresponds to object index in PSX file.</summary>
    public required int Index { get; init; }

    /// <summary>Parent bone name from the "//   parent:" comment, or null for root bones.</summary>
    public string? ParentName { get; init; }
}
