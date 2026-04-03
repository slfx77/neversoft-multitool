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

/// <summary>
///     Flat-array mesh data extracted from a glTF primitive, ready for rendering.
///     Mirrors Xbox360MemoryCarver's RenderableSubmesh layout.
/// </summary>
internal sealed class RenderSubmesh
{
    /// <summary>X, Y, Z per vertex (length = vertexCount * 3).</summary>
    public required float[] Positions { get; init; }

    /// <summary>3 indices per triangle (length = triangleCount * 3).</summary>
    public required int[] Triangles { get; init; }

    /// <summary>X, Y, Z per vertex (optional, for shading). Same length as Positions.</summary>
    public float[]? Normals { get; init; }

    /// <summary>R, G, B, A per vertex, 0-255 (optional). Length = vertexCount * 4.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>Whether this primitive's material is double-sided.</summary>
    public bool IsDoubleSided { get; init; }

    /// <summary>U, V per vertex (length = vertexCount * 2). Null if no UVs.</summary>
    public float[]? TexCoords { get; init; }

    /// <summary>Decoded RGBA pixels of the base color texture. Null if untextured.</summary>
    public byte[]? TextureData { get; init; }

    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }

    /// <summary>Material base color factor (linear RGBA, default white).</summary>
    public float BaseColorR { get; init; } = 1f;
    public float BaseColorG { get; init; } = 1f;
    public float BaseColorB { get; init; } = 1f;
    public float BaseColorA { get; init; } = 1f;

    /// <summary>0 = OPAQUE, 1 = MASK.</summary>
    public int AlphaMode { get; init; }

    public float AlphaCutoff { get; init; } = 0.5f;

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
}

/// <summary>
///     All renderable geometry loaded from a GLB file with bounding box.
/// </summary>
internal sealed class RenderScene
{
    public List<RenderSubmesh> Submeshes { get; } = [];
    public float MinX { get; set; } = float.MaxValue;
    public float MinY { get; set; } = float.MaxValue;
    public float MinZ { get; set; } = float.MaxValue;
    public float MaxX { get; set; } = float.MinValue;
    public float MaxY { get; set; } = float.MinValue;
    public float MaxZ { get; set; } = float.MinValue;

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Depth => MaxZ - MinZ;
    public bool HasGeometry => Submeshes.Count > 0;

    public void ExpandBounds(float[] positions)
    {
        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (z < MinZ) MinZ = z;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            if (z > MaxZ) MaxZ = z;
        }
    }
}
