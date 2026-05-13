using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Sector: checksum + bone_index + flags + CGeom (meshes with per-mesh vertex data).
///     Sector flags control vertex attribute presence (normals, colors, weights, etc.).
/// </summary>
public sealed class XbxSector
{
    public uint Checksum { get; init; }
    public int BoneIndex { get; init; }
    public int Flags { get; init; }

    // CGeom bounding volumes
    public Vector3 BboxMin { get; init; }
    public Vector3 BboxMax { get; init; }
    public Vector3 BsphereCenter { get; init; }
    public float BsphereRadius { get; init; }

    // Per-mesh data (each mesh has its own vertex buffer)
    public required XbxMesh[] Meshes { get; init; }

    // Sector flags
    public bool HasTexCoords => (Flags & 0x01) != 0;
    public bool HasVertexColors => (Flags & 0x02) != 0;
    public bool HasNormals => (Flags & 0x04) != 0;
    public bool IsSkinned => (Flags & 0x10) != 0;
    public bool HasBillboard => (Flags & 0x00800000) != 0;

    public int TotalTriangles => Meshes.Sum(m => m.TriangleCount);
    public int TotalVertices => Meshes.Sum(m => m.Vertices.Length);
}
