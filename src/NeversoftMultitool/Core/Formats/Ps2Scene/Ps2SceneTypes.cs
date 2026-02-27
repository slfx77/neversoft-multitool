using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
/// Parsed PS2 scene file (.mdl.ps2, .skin.ps2, .iskin.ps2).
/// Native PS2 format with version-triple header:
/// THPS4 (3,4,1), THUG (5,6,1), THUG2 (6,6,1).
/// </summary>
public sealed class Ps2Scene
{
    public required int MaterialVersion { get; init; }
    public required int MeshVersion { get; init; }
    public required int VertexVersion { get; init; }
    public required List<Ps2Material> Materials { get; init; }
    public required List<Ps2MeshGroup> MeshGroups { get; init; }
}

/// <summary>
/// Single-pass material in native PS2 format.
/// Unlike the cross-platform format, PS2 materials have one texture per material
/// rather than multiple rendering passes.
/// </summary>
public sealed class Ps2Material
{
    public uint Checksum { get; init; }
    public uint Flags { get; init; }
    public uint TextureChecksum { get; init; }
    public uint GroupChecksum { get; init; }
    public int AlphaRef { get; init; }
    public bool ClampU { get; init; }
    public bool ClampV { get; init; }

    /// <summary>
    /// Raw GS ALPHA register (u64). Encodes blend equation: Output = (A-B)*C + D.
    /// Bits 0-1: A, 2-3: B, 4-5: C, 6-7: D, 32-39: FIX.
    /// Sources: 0=Cs(source), 1=Cd(dest), 2=0. C sources: 0=As, 1=Ad, 2=FIX.
    /// </summary>
    public ulong RegAlpha { get; init; }

    /// <summary>
    /// Returns the fixed-blend opacity (0-1) if RegALPHA uses FIXED_BLEND mode:
    /// (Cs-Cd)*FIX/128 + Cd (A=0,B=1,C=2,D=1). Returns null for other modes.
    /// </summary>
    public float? FixedBlendOpacity
    {
        get
        {
            var a = (int)(RegAlpha & 0x3);
            var b = (int)((RegAlpha >> 2) & 0x3);
            var c = (int)((RegAlpha >> 4) & 0x3);
            var d = (int)((RegAlpha >> 6) & 0x3);
            var fix = (int)((RegAlpha >> 32) & 0xFF);
            // (Cs-Cd)*FIX + Cd = standard alpha blend with fixed alpha
            if (a == 0 && b == 1 && c == 2 && d == 1 && fix < 128)
                return fix / 128f;
            return null;
        }
    }
}

/// <summary>
/// Mesh group containing one or more meshes.
/// Each group has a checksum identifier used for name resolution.
/// </summary>
public sealed class Ps2MeshGroup
{
    public uint Checksum { get; init; }
    public required List<Ps2Mesh> Meshes { get; init; }
}

/// <summary>
/// Individual mesh with material reference and vertex data.
/// Triangle strips are encoded via ADC bits in vertex data — no separate index array.
/// Vertices are always per-mesh (all game versions).
/// </summary>
public sealed class Ps2Mesh
{
    public uint Checksum { get; init; }
    public uint MaterialChecksum { get; init; }
    public Ps2MeshFlags MeshFlags { get; init; }
    public Vector4 BoundingSphere { get; init; }
    public required Ps2Vertex[] Vertices { get; init; }
}

/// <summary>
/// Vertex with position, normal, color, UV, and ADC strip restart flag.
/// All values are pre-converted to float (skinned sint16 positions and UVs are decoded).
/// </summary>
public readonly struct Ps2Vertex(
    Vector3 position, Vector3 normal,
    byte r, byte g, byte b, byte a,
    float u, float v,
    bool hasNormal, bool hasColor, bool hasUV,
    bool isStripRestart)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Normal = normal;
    public readonly byte R = r, G = g, B = b, A = a;
    public readonly float U = u, V = v;
    public readonly bool HasNormal = hasNormal;
    public readonly bool HasColor = hasColor;
    public readonly bool HasUV = hasUV;

    /// <summary>
    /// ADC strip restart flag. When true, the GS does not draw a triangle
    /// at this vertex, effectively starting a new triangle strip.
    /// </summary>
    public readonly bool IsStripRestart = isStripRestart;
}

/// <summary>
/// Per-mesh attribute flags from THUG source mesh.h.
/// </summary>
[Flags]
public enum Ps2MeshFlags : uint
{
    Texture = 1 << 0,
    Colours = 1 << 1,
    Normals = 1 << 2,
    St16 = 1 << 3,
    Skinned = 1 << 4,
}

/// <summary>
/// Material flags from THUG source material.h.
/// </summary>
[Flags]
public enum Ps2MaterialFlags : uint
{
    UvWibble = 1 << 0,
    VcWibble = 1 << 1,
    Textured = 1 << 2,
    Environment = 1 << 3,
    Decal = 1 << 4,
    Smooth = 1 << 5,
    Transparent = 1 << 6,
    AnimatedTexture = 1 << 11,
    IgnoreVertexAlpha = 1 << 12,
}
