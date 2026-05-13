using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

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

    public Ps2WorldzoneConverter.WorldzoneTimeOfDay WorldzoneTimeOfDay { get; init; } =
        Ps2WorldzoneConverter.WorldzoneTimeOfDay.All;

    public float WorldzoneScale { get; init; } = 1f;
}
