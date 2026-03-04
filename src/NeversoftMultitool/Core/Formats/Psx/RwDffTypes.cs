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

    /// <summary>
    ///     PS2 GS ALPHA register blend mode byte from Neversoft BSP material extension.
    ///     Decoded as: A(2)|B(2)|C(2)|D(2) where A,B,D∈{Cs,Cd,0} and C∈{As,Ad,FIX}.
    ///     Key values: 0x00=opaque, 0x44=alpha blend, 0x48=additive, 0x68=additive+FIX,
    ///     0x42=subtractive, 0x62=subtractive+FIX.
    /// </summary>
    public byte GsAlpha { get; init; }

    /// <summary>
    ///     FIX value for fixed-factor blend modes (GsAlpha 0x68, 0x62, 0x64).
    ///     Range 0-128 where 128 = full intensity.
    /// </summary>
    public byte GsAlphaFix { get; init; }

    /// <summary>True if GsAlpha indicates additive blending (Cs*factor + Cd).</summary>
    public bool IsAdditive => GsAlpha is 0x48 or 0x68;

    /// <summary>True if GsAlpha indicates subtractive blending (Cd - Cs*factor).</summary>
    public bool IsSubtractive => GsAlpha is 0x42 or 0x62;

    /// <summary>
    ///     True if the GS ALPHA formula involves Cd (destination color), meaning it's a real
    ///     blending operation. Decoded from A(2)|B(2)|C(2)|D(2) where A,B,D∈{Cs=0,Cd=1,0=2}.
    ///     False for degenerate values like 0x0A/0x20/0x2A (formula = Cs = opaque)
    ///     and invisible values like 0x80/0x8A/0xA0 (formula = 0).
    /// </summary>
    public bool IsBlend
    {
        get
        {
            if (GsAlpha == 0) return false;
            int fieldA = GsAlpha & 0x03;
            int fieldB = (GsAlpha >> 2) & 0x03;
            int fieldD = (GsAlpha >> 6) & 0x03;
            return fieldA == 1 || fieldB == 1 || fieldD == 1;
        }
    }
}

/// <summary>
///     Atomic linking a frame index to a geometry index.
/// </summary>
public sealed class RwAtomic
{
    public required int FrameIndex { get; init; }
    public required int GeometryIndex { get; init; }
    public required int Flags { get; init; }
    public RwSkinData? SkinData { get; init; }
}

/// <summary>
///     Single bone from RW Skin PLG (0x0116).
///     76 bytes: id(u32) + index(u32) + flags(u32) + 4×4 inverse bind matrix.
/// </summary>
public readonly struct RwSkinBone(int id, int index, int flags, Matrix4x4 inverseBindMatrix)
{
    public readonly int Id = id, Index = index, Flags = flags;
    public readonly Matrix4x4 InverseBindMatrix = inverseBindMatrix;
}

/// <summary>
///     Skinning data from RW Skin PLG (0x0116).
///     Per-vertex bone indices (4×u8) and weights (4×f32), plus per-bone inverse bind matrices.
///     THPS3 PS2 layout: header(8B) + boneIndices(N×4) + boneWeights(N×16) + bones(B×76).
/// </summary>
public sealed class RwSkinData
{
    public required int NumBones { get; init; }
    public required int NumVertices { get; init; }
    public required byte[] BoneIndices { get; init; }
    public required float[] BoneWeights { get; init; }
    public required RwSkinBone[] Bones { get; init; }
}
