using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Parsed RenderWare 3.x DFF (Clump) file.
///     Contains frame hierarchy, geometries, materials, and atomic links.
///     Used for THPS3 PS2 .SKN files (version 0x0310).
/// </summary>
public sealed class RwDffClump
{
    public required RwFrame[] Frames { get; init; }
    public required RwGeometry[] Geometries { get; init; }
    public required RwAtomic[] Atomics { get; init; }
}

/// <summary>
///     Frame in the RW frame hierarchy (FrameList).
///     Contains local rotation matrix (3×3), position, and parent index.
/// </summary>
public sealed class RwFrame
{
    public required Matrix4x4 LocalTransform { get; init; }
    public required int ParentIndex { get; init; }
    public required int Flags { get; init; }
}

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

public readonly struct RwVertexColor(byte r, byte g, byte b, byte a)
{
    public readonly byte R = r, G = g, B = b, A = a;
}

/// <summary>
///     Triangle with three vertex indices and a material index.
///     RW binary stores as (v2, v1, materialId, v3); parsed to (v0, v1, v2, materialIndex).
/// </summary>
public readonly struct RwTriangle(int v0, int v1, int v2, int materialIndex)
{
    public readonly int V0 = v0, V1 = v1, V2 = v2;
    public readonly int MaterialIndex = materialIndex;
}

public sealed class RwMaterial
{
    public required byte R { get; init; }
    public required byte G { get; init; }
    public required byte B { get; init; }
    public required byte A { get; init; }
    public required string? TextureName { get; init; }
    public required string? MaskName { get; init; }
    public float Ambient { get; init; }
    public float Specular { get; init; }
    public float Diffuse { get; init; }
}

/// <summary>
///     Atomic linking a frame index to a geometry index.
/// </summary>
public sealed class RwAtomic
{
    public required int FrameIndex { get; init; }
    public required int GeometryIndex { get; init; }
    public required int Flags { get; init; }
}
