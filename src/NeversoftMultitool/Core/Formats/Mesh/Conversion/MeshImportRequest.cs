using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed class MeshImportRequest
{
    public required AssetSource Source { get; init; }
    public required string FileName { get; init; }
    public required string OutputStem { get; init; }
    public required ModelSourceKind SourceKind { get; init; }
    public Ps2SceneSubFormat Ps2SubFormat { get; init; }
    public bool HasPlacedPsxCompanion { get; init; }
    public string? TexturePath { get; init; }
    public string? SkeletonPath { get; init; }
    public string? DdxPath { get; init; }
    public string? PsxPath { get; init; }
    public string? DdmTexturePath { get; init; }

    public WorldzoneTimeOfDay WorldzoneTimeOfDay { get; init; } =
        WorldzoneTimeOfDay.All;

    public float WorldzoneScale { get; init; } = 1f;

    /// <summary>
    ///     Pre-decoded SKA animation slots, populated into <see cref="ModelDocument.Animations" />
    ///     by the PS2 Scene and RW DFF parsers. Null = no animations to embed.
    /// </summary>
    public IReadOnlyList<(string Name, SkaAnimation Animation)>? SkaAnimations { get; init; }

    /// <summary>
    ///     Optional pre-loaded skeleton override for PS2 Scene parsing. When set, the
    ///     parser uses this instead of re-loading from <see cref="SkeletonPath" />.
    ///     Lets callers like <c>SkaCommand</c> apply THPS4 V1 default-pose enrichment
    ///     upstream and preserve it through the parser.
    /// </summary>
    public Ps2Skeleton? PreparedSkeleton { get; init; }
}
