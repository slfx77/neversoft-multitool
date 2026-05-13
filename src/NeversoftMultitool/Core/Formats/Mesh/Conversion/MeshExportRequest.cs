using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

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
