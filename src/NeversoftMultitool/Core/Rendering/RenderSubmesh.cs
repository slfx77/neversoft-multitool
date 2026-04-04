namespace NeversoftMultitool.Core.Rendering;

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
