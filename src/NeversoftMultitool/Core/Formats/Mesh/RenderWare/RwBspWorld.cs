namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

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
