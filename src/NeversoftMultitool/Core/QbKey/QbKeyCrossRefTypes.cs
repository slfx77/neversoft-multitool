namespace NeversoftMultitool.Core;

/// <summary>
///     Source type for a discovered name-to-hash mapping.
/// </summary>
public enum QbKeyMappingSource
{
    ObjectName,
    TextureName,
    MaterialName,
    DetailTextureName,
    CubemapName,
    ArchiveFilename,
    PshPartName
}

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

/// <summary>
///     Result of cross-referencing one DDM/PSX file pair.
/// </summary>
public sealed class CrossRefFileResult
{
    public required string DdmFile { get; init; }
    public required string PsxFile { get; init; }
    public required List<QbKeyMapping> Matches { get; init; }
    public required List<string> UnmatchedDdmNames { get; init; }
    public required List<uint> UnmatchedPsxHashes { get; init; }
    public int DdmNameCount { get; init; }
    public int PsxHashCount { get; init; }
    public int MeshHashCount { get; init; }
    public int TextureHashCount { get; init; }
    public int MeshMatches { get; init; }
    public int TextureMatches { get; init; }
}

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
