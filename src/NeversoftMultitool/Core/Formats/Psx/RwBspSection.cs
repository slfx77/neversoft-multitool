using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     A leaf sector (AtomicSection) in the BSP tree, containing mesh geometry data.
///     Each section has its own vertex/triangle data and references materials from the World's shared list.
/// </summary>
public sealed class RwBspSection
{
    public required int MatListWindowBase { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required Vector3[]? Normals { get; init; }
    public required RwVertexColor[]? Colors { get; init; }
    public required Vector2[]? UVs { get; init; }
    public required RwTriangle[] Triangles { get; init; }
}
