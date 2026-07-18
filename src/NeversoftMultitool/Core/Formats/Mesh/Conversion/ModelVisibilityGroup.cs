namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Describes a source-authored geometry choice which can be applied when
///     rebuilding a <see cref="ModelDocument" />. The descriptor is a value
///     snapshot; callers keep their own override dictionary rather than
///     mutating the generated document.
/// </summary>
public sealed record ModelVisibilityGroup
{
    /// <summary>
    ///     Stable identifier used as the key in
    ///     <see cref="MeshImportRequest.VisibilityOverrides" />.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>A concise source-derived name suitable for display.</summary>
    public required string Label { get; init; }

    /// <summary>The visibility state authored for the initial runtime state.</summary>
    public required bool DefaultEnabled { get; init; }

    /// <summary>The state used to generate this document.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>How the source asset expressed this choice.</summary>
    public required ModelVisibilityGroupSource Source { get; init; }

    /// <summary>
    ///     Source-level detail useful for diagnostics and richer UI text, such
    ///     as a TRG command range or the paired PSX object indices.
    /// </summary>
    public required string SourceReference { get; init; }

    /// <summary>
    ///     Stable identifier shared by choices which cannot be enabled at the
    ///     same time. Null means this Boolean group is independent. The
    ///     default geometry is represented by every choice in a set being off.
    /// </summary>
    public string? ExclusiveSetId { get; init; }
}

public enum ModelVisibilityGroupSource
{
    TriggerRange,
    AlternateGeometry
}
