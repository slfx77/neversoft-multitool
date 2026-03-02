using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Parsed RenderWare 3.x World (BSP) file.
///     Contains shared materials and a list of atomic (leaf) sectors with geometry data.
///     Used for THPS3 PS2 level BSP files (version 0x0310).
/// </summary>
public sealed class RwBspWorld
{
    public required int FormatFlags { get; init; }
    public required int TotalTriangles { get; init; }
    public required int TotalVertices { get; init; }
    public required RwMaterial[] Materials { get; init; }
    public required RwBspSection[] Sections { get; init; }
}

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
