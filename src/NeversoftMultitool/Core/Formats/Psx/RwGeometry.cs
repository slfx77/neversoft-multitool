using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Geometry containing vertices, normals, UVs, triangles, and materials.
/// </summary>
public sealed class RwGeometry
{
    public required int Flags { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required Vector3[]? Normals { get; init; }
    public required Vector2[]? UVs { get; init; }
    public required RwVertexColor[]? Colors { get; init; }
    public required RwTriangle[] Triangles { get; init; }
    public required RwMaterial[] Materials { get; init; }
    public required Vector4 BoundingSphere { get; init; }
}
