namespace NeversoftMultitool;

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

    internal bool IsPsx => Format == "PSX";

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
