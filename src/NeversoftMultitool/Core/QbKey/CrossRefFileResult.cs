namespace NeversoftMultitool.Core.QbKey;

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
