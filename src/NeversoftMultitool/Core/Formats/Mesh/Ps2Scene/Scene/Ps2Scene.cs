#pragma warning disable CA1051 // Do not declare visible instance fields — Ps2Vertex is a readonly struct data carrier

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

/// <summary>
///     Parsed PS2 scene file (.mdl.ps2, .skin.ps2, .iskin.ps2).
///     Native PS2 format with version-triple header:
///     THPS4 (3,4,1), THUG (5,6,1), THUG2 (6,6,1).
/// </summary>
public sealed class Ps2Scene
{
    public required int MaterialVersion { get; init; }
    public required int MeshVersion { get; init; }
    public required int VertexVersion { get; init; }
    public required List<Ps2Material> Materials { get; init; }
    public required List<Ps2MeshGroup> MeshGroups { get; init; }
}
