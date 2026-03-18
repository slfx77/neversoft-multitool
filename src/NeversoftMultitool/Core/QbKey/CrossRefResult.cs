namespace NeversoftMultitool.Core;

/// <summary>
///     Aggregate result of a cross-reference run across all file pairs.
/// </summary>
public sealed class CrossRefResult
{
    public required List<CrossRefFileResult> FileResults { get; init; }
    public required Dictionary<string, uint> AllDiscoveredMappings { get; init; }
    public int TotalDdmFiles { get; init; }
    public int TotalPsxFiles { get; init; }
    public int MatchedFilePairs { get; init; }
    public int TotalDdmNames { get; init; }
    public int TotalPsxHashes { get; init; }
    public int TotalMatches { get; init; }
    public int NewDiscoveries { get; init; }
    public int TotalMeshHashes { get; init; }
    public int TotalTextureHashes { get; init; }
    public int TotalMeshMatches { get; init; }
    public int TotalTextureMatches { get; init; }
}
