using System.Numerics;

namespace DdmAnalyzer.Commands;

internal sealed class LevelAnalysis
{
    public required string LevelName { get; init; }
    public required string LevelDir { get; init; }

    public int LevelDdmCount { get; set; }
    public int ObjectsDdmCount { get; set; }
    public int LevelPsxCount { get; set; }
    public int ObjectsPsxCount { get; set; }
    public int LevelMeshHashes { get; set; }
    public int ObjectsMeshHashes { get; set; }
    public int DdxTextureCount { get; set; }
    public int LightCount { get; set; }
    public bool HasLitFile { get; set; }

    public int LevelMatched { get; set; }
    public int LevelUnmatched { get; set; }
    public int LevelUnplaced { get; set; }
    public List<PlacedEntry> LevelPlaced { get; set; } = [];
    public List<UnmatchedEntry> LevelUnmatchedEntries { get; set; } = [];
    public List<UnplacedEntry> LevelUnplacedEntries { get; set; } = [];

    public int ObjectsMatched { get; set; }
    public int ObjectsUnmatched { get; set; }
    public int ObjectsUnplaced { get; set; }
    public List<PlacedEntry> ObjectsPlaced { get; set; } = [];
    public List<UnmatchedEntry> ObjectsUnmatchedEntries { get; set; } = [];
    public List<UnplacedEntry> ObjectsUnplacedEntries { get; set; } = [];

    public List<PlacedEntry> LevelOutliers { get; set; } = [];
    public List<PlacedEntry> ObjectsOutliers { get; set; } = [];
    public BoundingBox? LevelBounds { get; set; }
    public BoundingBox? ObjectsBounds { get; set; }
}

internal record struct PlacedEntry(string Name, uint Hash, float X, float Y, float Z, int DdmIndex);

internal record struct UnmatchedEntry(uint Hash, int MeshIndex, float X, float Y, float Z, string? ResolvedName);

internal record struct UnplacedEntry(string Name, uint Checksum, int DdmIndex);

internal record struct BoundingBox(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
{
    public readonly Vector3 Center => new(
        (MinX + MaxX) / 2,
        (MinY + MaxY) / 2,
        (MinZ + MaxZ) / 2);

    public readonly Vector3 Extent => new(
        MaxX - MinX,
        MaxY - MinY,
        MaxZ - MinZ);
}
