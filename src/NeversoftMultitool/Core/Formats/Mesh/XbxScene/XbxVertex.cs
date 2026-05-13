using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Per-vertex data decoded from interleaved vertex buffer.
///     Skinned vertices preserve packed weight/index data when available so it can be
///     reapplied onto companion meshes in other platform formats.
///     Non-skinned: pos(3f) + [normal(3f)] + [color(4B)] + UVs.
/// </summary>
public struct XbxVertex
{
    public Vector3 Position { get; set; }
    public Vector3 Normal { get; set; }
    public Vector4 Color { get; set; } // BGRA ÷128, default white
    public Vector2 TexCoord { get; set; } // First UV set only
    public bool HasNormal { get; set; }
    public bool HasColor { get; set; }
    public bool HasSkinData { get; set; }
    public int BoneIndex0 { get; set; }
    public int BoneIndex1 { get; set; }
    public int BoneIndex2 { get; set; }
    public int BoneIndex3 { get; set; }
    public float BoneWeight0 { get; set; }
    public float BoneWeight1 { get; set; }
    public float BoneWeight2 { get; set; }
    public float BoneWeight3 { get; set; }
}
