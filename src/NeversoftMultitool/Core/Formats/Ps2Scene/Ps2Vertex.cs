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
    public Vector3 Position { get; } = position;
    public Vector3 Normal { get; } = normal;
    public byte R { get; } = r;
    public byte G { get; } = g;
    public byte B { get; } = b;
    public byte A { get; } = a;
    public float U { get; } = u;
    public float V { get; } = v;
    public bool HasNormal { get; } = hasNormal;
    public bool HasColor { get; } = hasColor;
    public bool HasUV { get; } = hasUV;

    /// <summary>
    ///     ADC/no-kick flag. When true, the GS updates the vertex queue with this
    ///     vertex but suppresses the draw kick for the current step.
    /// </summary>
    public bool IsStripRestart { get; } = isStripRestart;

    /// <summary>Bone indices for skinned vertices (up to 3 influences).</summary>
    public int BoneIndex0 { get; } = boneIndex0;
    public int BoneIndex1 { get; } = boneIndex1;
    public int BoneIndex2 { get; } = boneIndex2;

    /// <summary>Bone weights for skinned vertices (up to 3 influences, sum to ~1.0).</summary>
    public float BoneWeight0 { get; } = boneWeight0;
    public float BoneWeight1 { get; } = boneWeight1;
    public float BoneWeight2 { get; } = boneWeight2;

    /// <summary>Whether this vertex has bone weight/index data for skinning.</summary>
    public bool HasSkinData { get; } = hasSkinData;
}
