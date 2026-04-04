using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

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
