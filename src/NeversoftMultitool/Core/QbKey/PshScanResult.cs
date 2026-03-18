namespace NeversoftMultitool.Core;

/// <summary>
///     Result of scanning .psh header files for mesh part names.
/// </summary>
public sealed class PshScanResult
{
    public required List<QbKeyMapping> Matches { get; init; }
    public int TotalPshFiles { get; init; }
    public int TotalCandidateNames { get; init; }
    public int TotalMeshHashes { get; init; }
    public int NewDiscoveries { get; init; }
}
