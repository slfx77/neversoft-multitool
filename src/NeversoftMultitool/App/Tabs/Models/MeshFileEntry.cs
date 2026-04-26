using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public class MeshFileEntry : BaseFileEntry
{
    private int _triangleCount;

    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string Format { get; init; }
    public required int ObjectCount { get; init; }
    public required int MeshCount { get; init; }

    // Read bytes + resolve companions uniformly — filesystem or archive-backed.
    // Converter goes through this instead of opening FilePath directly.
    public required AssetSource Source { get; init; }

    // Display path relative to the browsed root. Falls back to FileName for
    // single-file inputs or when the scanner didn't populate it.
    public string RelativePath { get; init; } = "";

    protected override string ProcessingVerb => "Converting...";

    // Internal: scan-time hint for DDM placed-level detection.
    // Archive sources may set this false even if a sibling exists in the archive; we
    // only surface "DDM (placed)" when the scanner actually sees a matching PSX entry.
    internal bool HasPlacedPsxCompanion { get; init; }

    // Internal: PS2 scene sub-format routing
    internal Ps2SceneSubFormat Ps2SubFormat { get; init; }

    internal bool IsPlacedLevel => HasPlacedPsxCompanion;

    internal bool IsPsx => Format == "PSX";
    internal bool IsRwDff => Format == "RW DFF";
    internal bool IsRwBsp => Format == "RW BSP";
    internal bool IsCol => Format == "COL";

    internal bool IsPakWorldzone => Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone;

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
