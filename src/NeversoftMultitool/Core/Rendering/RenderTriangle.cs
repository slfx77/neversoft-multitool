using System.Runtime.InteropServices;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Screen-projected triangle with per-vertex data for rasterization.
///     Ported from Xbox360MemoryCarver's TriangleData, stripped to essentials.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal struct RenderTriangle
{
    // Screen-space positions (after projection) and depth
    public float Sx0, Sy0, Z0;
    public float Sx1, Sy1, Z1;
    public float Sx2, Sy2, Z2;

    // Per-vertex normals (view space, for shading)
    public float Nx0, Ny0, Nz0;
    public float Nx1, Ny1, Nz1;
    public float Nx2, Ny2, Nz2;
    public bool HasNormals;

    // Per-vertex colors (0-255 RGBA)
    public byte R0, G0, B0, A0;
    public byte R1, G1, B1, A1;
    public byte R2, G2, B2, A2;
    public bool HasVertexColors;

    // Per-vertex texture coordinates
    public float U0, V0;
    public float U1, V1;
    public float U2, V2;

    // Index into RenderScene.Submeshes for texture/material lookup
    public int SubmeshIndex;

    // Flat shade fallback (precomputed when no per-vertex normals)
    public float FlatShade;

    public bool IsDoubleSided;
}
