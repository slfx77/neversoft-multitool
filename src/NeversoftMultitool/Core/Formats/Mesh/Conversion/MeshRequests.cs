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

public sealed class MeshExportRequest
{
    public required string OutputDirectory { get; init; }
    public MeshOutputFormat Format { get; init; } = MeshOutputFormat.Glb;
    public string? OutputStem { get; init; }
    public string? BlenderHelperPath { get; init; }
    public Ps2WorldzoneConverter.WorldzoneTimeOfDay WorldzoneTimeOfDay { get; init; } =
        Ps2WorldzoneConverter.WorldzoneTimeOfDay.All;
    public float WorldzoneScale { get; init; } = 1f;
    public CancellationToken CancellationToken { get; init; }
}

public sealed class MeshExportResult
{
    public required IReadOnlyList<string> OutputPaths { get; init; }
    public int Triangles { get; init; }
    public int MaterialCount { get; init; }
    public int TextureCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static MeshExportResult Empty { get; } = new()
    {
        OutputPaths = [],
        Warnings = []
    };
}

public interface IModelParser
{
    ModelDocument Parse(MeshImportRequest request);
}

public interface IModelExporter
{
    MeshExportResult Export(ModelDocument document, MeshExportRequest request);
}
