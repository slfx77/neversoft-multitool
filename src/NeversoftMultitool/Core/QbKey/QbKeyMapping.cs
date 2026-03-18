namespace NeversoftMultitool.Core;

/// <summary>
///     A single discovered name-to-hash mapping from DDM/PSX cross-reference.
/// </summary>
public sealed class QbKeyMapping
{
    public required string Name { get; init; }
    public required uint Hash { get; init; }
    public required string SourceFile { get; init; }
    public required QbKeyMappingSource Source { get; init; }
}
