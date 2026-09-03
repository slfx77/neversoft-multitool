using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

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

    /// <summary>
    ///     Neversoft's raw per-triangle collision flags, in exactly the same
    ///     order as <see cref="Triangles"/>. THPS3 stores these after the
    ///     atomic-sector STRUCT in its <c>0x0294AF01</c> extension payload:
    ///     version 6 followed by one little-endian <c>u16</c> per triangle.
    ///     An empty array means that the optional extension was absent,
    ///     malformed, ambiguous, or a different version; ordinary render
    ///     geometry remains usable in that case, while a dedicated collision
    ///     view can fail closed.
    /// </summary>
    public ushort[] TriangleCollisionFlags { get; init; } = [];
}
