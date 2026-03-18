namespace NeversoftMultitool.Core;

/// <summary>
///     Result of scanning archive filenames against PSX texture hashes.
/// </summary>
public sealed class ArchiveScanResult
{
    public required List<QbKeyMapping> TextureMatches { get; init; }
    public required List<QbKeyMapping> MeshMatches { get; init; }
    public int TotalCandidateNames { get; init; }
    public int TotalTextureHashes { get; init; }
    public int TotalMeshHashes { get; init; }
    public int NewDiscoveries { get; init; }
    public int ArchivesScanned { get; init; }
    public int ArchiveErrors { get; init; }

    public List<QbKeyMapping> AllMatches
    {
        get
        {
            var all = new List<QbKeyMapping>(TextureMatches);
            all.AddRange(MeshMatches);
            return all;
        }
    }
}
