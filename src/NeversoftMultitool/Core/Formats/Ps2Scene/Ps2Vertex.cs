using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Vertex with position, normal, color, UV, skinning data, and ADC strip restart flag.
///     All values are pre-converted to float (skinned sint16 positions and UVs are decoded).
/// </summary>
public readonly struct Ps2Vertex(
    Vector3 position,
    Vector3 normal,
    byte r,
    byte g,
    byte b,
    byte a,
    float u,
    float v,
    bool hasNormal,
    bool hasColor,
    bool hasUV,
    bool isStripRestart,
    int boneIndex0 = 0,
    int boneIndex1 = 0,
    int boneIndex2 = 0,
    float boneWeight0 = 0,
    float boneWeight1 = 0,
    float boneWeight2 = 0,
    bool hasSkinData = false)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Normal = normal;
    public readonly byte R = r, G = g, B = b, A = a;
    public readonly float U = u, V = v;
    public readonly bool HasNormal = hasNormal;
    public readonly bool HasColor = hasColor;
    public readonly bool HasUV = hasUV;

    /// <summary>
    ///     ADC/no-kick flag. When true, the GS updates the vertex queue with this
    ///     vertex but suppresses the draw kick for the current step.
    /// </summary>
    public readonly bool IsStripRestart = isStripRestart;

    /// <summary>Bone indices for skinned vertices (up to 3 influences).</summary>
    public readonly int BoneIndex0 = boneIndex0, BoneIndex1 = boneIndex1, BoneIndex2 = boneIndex2;

    /// <summary>Bone weights for skinned vertices (up to 3 influences, sum to ~1.0).</summary>
    public readonly float BoneWeight0 = boneWeight0, BoneWeight1 = boneWeight1, BoneWeight2 = boneWeight2;

    /// <summary>Whether this vertex has bone weight/index data for skinning.</summary>
    public readonly bool HasSkinData = hasSkinData;
}
