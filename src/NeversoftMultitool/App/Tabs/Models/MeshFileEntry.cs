namespace NeversoftMultitool;

internal enum Ps2SceneSubFormat
{
    None,
    Standard,
    ThawSkin,
    PakSkin,
    PakMdl,
    Geom
}

public class MeshFileEntry : BaseFileEntry
{
    private int _triangleCount;

    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string Format { get; init; }
    public required int ObjectCount { get; init; }
    public required int MeshCount { get; init; }

    protected override string ProcessingVerb => "Converting...";

    // Internal: PSX level geometry companion texture library (*_g.psx → *_l.psx)
    internal string? CompanionLibraryPsxPath { get; init; }

    // Internal: DDM placement companions
    internal string? CompanionPsxPath { get; init; }
    internal string? CompanionObjectsDdmPath { get; init; }
    internal bool IsPlacedLevel => CompanionPsxPath != null;

    // Internal: PS2 scene sub-format routing
    internal Ps2SceneSubFormat Ps2SubFormat { get; init; }

    // Internal: companion skeleton for PS2 skinned meshes
    internal string? CompanionSkeletonPath { get; init; }

    // Internal: companion texture file for PS2/Xbox scenes
    internal string? CompanionTexPath { get; init; }

    internal bool IsPsx => Format == "PSX";
    internal bool IsRwDff => Format == "RW DFF";
    internal bool IsRwBsp => Format == "RW BSP";
    internal bool IsCol => Format == "COL";

    internal bool IsPs2Scene => Format is "PS2 (THPS4)" or "PS2 (THUG)"
        or "PS2 (THUG2)" or "PS2 (THAW)" or "PS2 (pre-compiled)";

    internal bool IsPs2Geom => Format == "PS2 GEOM";

    internal bool IsXbxScene => Format.StartsWith("Xbox (", StringComparison.Ordinal)
                                || Format.StartsWith("PC (", StringComparison.Ordinal);

    public string FormatDisplay => Format;
    public string ObjectsDisplay => ObjectCount.ToString("N0");
    public string MeshesDisplay => MeshCount.ToString("N0");

    public int TriangleCount
    {
        get => _triangleCount;
        set
        {
            _triangleCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrianglesDisplay));
        }
    }

    public string TrianglesDisplay => _triangleCount > 0 ? _triangleCount.ToString("N0") : "";
}
