using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Parsed Xbox/PC scene file (.skin.xbx, .mdl.xbx) from THUG2.
///     Multi-pass materials, per-sector CGeom with per-mesh interleaved vertex buffers.
///     Format spec from nxtools fmt_thscene_import.py + THUG source material.cpp.
/// </summary>
public sealed class XbxScene
{
    public required XbxMaterial[] Materials { get; init; }
    public required XbxSector[] Sectors { get; init; }
    public required XbxLink[] Links { get; init; }

    public int TotalTriangles => Sectors.Sum(s => s.TotalTriangles);
    public int TotalVertices => Sectors.Sum(s => s.TotalVertices);
}

public sealed class XbxMaterial
{
    public uint Checksum { get; init; }
    public uint NameChecksum { get; init; }
    public int NumPasses { get; init; }
    public int AlphaCutoff { get; init; }
    public bool Sorted { get; init; }
    public float DrawOrder { get; init; }
    public bool SingleSided { get; init; }
    public bool NoBfc { get; init; }
    public int ZBias { get; init; }
    public bool Grassify { get; init; }
    public float GrassHeight { get; init; }
    public int GrassLayers { get; init; }
    public float SpecularPower { get; init; }
    public Vector3 SpecularColor { get; init; }
    public required XbxPass[] Passes { get; init; }
}

public sealed class XbxPass
{
    public uint TextureChecksum { get; init; }
    public uint Flags { get; init; }
    public bool HasColor { get; init; }
    public Vector3 Color { get; init; }
    public uint BlendMode { get; init; }
    public uint FixedAlpha { get; init; }
    public uint UAddressing { get; init; }
    public uint VAddressing { get; init; }
    public Vector2 EnvmapTiling { get; init; }
    public uint FilteringMode { get; init; }
}

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

/// <summary>
///     Mesh within a sector: bounding volume, material reference, per-mesh vertex buffer,
///     and triangle strip index buffer. THUG2 uses per-mesh interleaved vertices.
/// </summary>
public sealed class XbxMesh
{
    // Bounding volumes
    public Vector3 BsphereCenter { get; init; }
    public float BsphereRadius { get; init; }
    public Vector3 BboxMin { get; init; }
    public Vector3 BboxMax { get; init; }

    public uint MeshFlags { get; init; }
    public uint MaterialChecksum { get; init; }

    // Per-mesh vertex data (from LOD 0 vertex buffer)
    public required XbxVertex[] Vertices { get; init; }

    /// <summary>Face indices from LOD 0 (degenerate triangle strip or pre-triangulated).</summary>
    public required ushort[] FaceIndices { get; init; }

    /// <summary>True if FaceIndices are pre-triangulated (every 3 = one triangle). THAW format.</summary>
    public bool IsPreTriangulated { get; init; }

    /// <summary>Triangle count from the highest-detail LOD.</summary>
    public int TriangleCount
    {
        get
        {
            if (FaceIndices.Length < 3) return 0;
            if (IsPreTriangulated) return FaceIndices.Length / 3;
            var count = 0;
            for (var i = 2; i < FaceIndices.Length; i++)
            {
                if (FaceIndices[i - 2] != FaceIndices[i - 1] &&
                    FaceIndices[i - 1] != FaceIndices[i] &&
                    FaceIndices[i - 2] != FaceIndices[i])
                    count++;
            }
            return count;
        }
    }
}

/// <summary>
///     Per-vertex data decoded from interleaved vertex buffer.
///     Skinned vertices: pos(3f) + weights(u32) + bones(4×u16) + packed_normal(u32) + color(4B) + UVs.
///     Non-skinned: pos(3f) + [normal(3f)] + [color(4B)] + UVs.
/// </summary>
public struct XbxVertex
{
    public Vector3 Position { get; set; }
    public Vector3 Normal { get; set; }
    public Vector4 Color { get; set; }       // BGRA ÷128, default white
    public Vector2 TexCoord { get; set; }    // First UV set only
    public bool HasNormal { get; set; }
    public bool HasColor { get; set; }
}

/// <summary>
///     Hierarchy link entry: sector-to-parent bone relationship with transform matrix.
///     Present in MDL files with multiple sectors (vehicles, multi-part objects).
/// </summary>
public sealed class XbxLink
{
    public uint SectorChecksum { get; init; }
    public uint ParentChecksum { get; init; }
    public ushort Index { get; init; }
    public Matrix4x4 Transform { get; init; }
}
