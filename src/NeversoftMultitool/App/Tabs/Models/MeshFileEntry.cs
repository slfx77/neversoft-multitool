using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool;

public class MeshFileEntry : BaseFileEntry
{
    private bool _isChecked = true;
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

    // Internal: PSX structure flag used by the animation panel. Older flat
    // supers (Apocalypse / THPS1) have no HIER table but are still animated
    // characters, so PsxIsSuperModel is the authoritative character gate.
    internal bool PsxIsSuperModel { get; init; }

    // Raw format classification is stable across archive/folder names and is
    // used for format-scale viewer tuning (notably Apocalypse v3 levels).
    internal PsxMeshFormatRevision PsxFormatRevision { get; init; }

    /// <summary>
    ///     Largest bounding radius across a carved N64 bundle's meshes, from its
    ///     <c>bounds.bin</c>; 0 when absent or the bundle is an authored-empty
    ///     stub. The MEASUREMENT is stored rather than a verdict so
    ///     <c>N64BundleClassifier</c> stays the single testable authority —
    ///     App code is excluded from the cross-platform target and cannot be
    ///     covered by tests.
    /// </summary>
    internal float N64MaxBoundsRadius { get; init; }

    /// <summary>
    ///     True only when the shell/render-bank pair passes the bounded N64
    ///     animation gate: embedded 0x2A/0x2C, every G_MTX in the global joint
    ///     range, and no unresolved global-versus-relative address ambiguity.
    /// </summary>
    internal bool N64HasEmbeddedAnimations { get; init; }

    // True only when the scanner found a companion that the current parser can
    // assemble with this mesh. Unsupported formats remain false even if they
    // happen to use a similarly named sibling file.
    internal bool HasSupportedLevelObjectCompanion { get; init; }

    /// <summary>
    ///     Scan-time content verdict for the manual Xbox/PC/GameCube skeleton
    ///     control. The sector flag is authoritative; filename suffixes only
    ///     route the parser and do not prove that a scene carries skin records.
    /// </summary>
    internal bool XbxHasSkinnedSectors { get; init; }

    /// <summary>
    ///     Per-entry, fully parsed manual skeleton selection. Replacing this
    ///     single immutable record keeps preview and batch snapshots coherent.
    /// </summary>
    internal XbxSkeletonSelection? XbxSkeletonSelection { get; set; }

    internal bool IsPlacedLevel => HasPlacedPsxCompanion;

    internal bool IsPsx => Format == "PSX";
    internal bool IsN64Model => Format == "N64";
    internal bool IsRwDff => Format == "RW DFF";
    internal bool IsRwBsp => Format == "RW BSP";
    internal bool IsCol => Format == "COL";

    internal bool IsPakWorldzone => Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone;

    // Skeleton resolution cache for the Animations panel (probed once per entry).
    internal int? SkeletonBoneCount { get; set; }
    internal bool SkeletonProbed { get; set; }

    /// <summary>
    ///     Formats that can drive the Animations panel: skinned meshes with a
    ///     resolvable skeleton (was the Character Preview tab's filter).
    /// </summary>
    internal bool IsAnimatableCharacter =>
        IsRwDff
        || (IsPs2Scene && Ps2SubFormat is Ps2SceneSubFormat.ThawSkin
            or Ps2SceneSubFormat.PakSkin
            or Ps2SceneSubFormat.Standard)
        || (IsPsx && PsxIsSuperModel)
        || (IsN64Model && N64HasEmbeddedAnimations);

    internal bool IsPs2Scene => Format is "PS2 (THPS4)" or "PS2 (THUG)"
        or "PS2 (THUG2)" or "PS2 (THAW)" or "PS2 (pre-compiled)";

    internal bool IsPs2Geom => Format == "PS2 GEOM";

    internal bool IsXbxScene => Format.StartsWith("Xbox (", StringComparison.Ordinal)
                                || Format.StartsWith("PC (", StringComparison.Ordinal)
                                || Format.StartsWith("GameCube (", StringComparison.Ordinal);

    internal bool SupportsExplicitXbxSkeleton =>
        XbxSkeletonEligibility.Supports(Format, XbxHasSkinnedSectors);

    public string FormatDisplay => Format;
    public string ObjectsDisplay => ObjectCount.ToString("N0");
    public string MeshesDisplay => MeshCount.ToString("N0");

    /// <summary>Included in batch Convert / Render runs (default: everything).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

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
