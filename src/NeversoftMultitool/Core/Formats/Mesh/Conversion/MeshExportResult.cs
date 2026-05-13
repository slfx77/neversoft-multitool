namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

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
