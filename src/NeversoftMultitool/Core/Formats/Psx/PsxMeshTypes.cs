namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     A PSX object entry (36 bytes). Contains world-space position and mesh index.
/// </summary>
public sealed class PsxMeshObject
{
    public uint Flags { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }
    public int RawZ { get; init; }
    public ushort MeshIndex { get; init; }
    public int ParentIndex { get; set; } = -1;

    /// <summary>
    ///     Item flag bit 1 (0x02) = character ("Super"). The game uses this to select
    ///     M3dAsm_TransformAndOutcodeSuperVertices (which divides vertices by 16)
    ///     vs M3dAsm_TransformAndOutcodeItemVertices (no division).
    /// </summary>
    public bool IsCharacter => (Flags & 0x02) != 0;

    public float X(float translationDivisor)
    {
        return RawX / (4096f * translationDivisor);
    }

    public float Y(float translationDivisor)
    {
        return RawY / (4096f * translationDivisor);
    }

    public float Z(float translationDivisor)
    {
        return RawZ / (4096f * translationDivisor);
    }
}

/// <summary>
///     A parsed mesh within a PSX file. Contains vertices, normals, and faces.
/// </summary>
public sealed class PsxMesh
{
    public required List<PsxVertex> Vertices { get; init; }
    public required List<PsxNormal> Normals { get; init; }
    public required List<PsxFace> Faces { get; init; }
    public short LodDepth { get; init; }
    public ushort LodNextMeshIndex { get; init; }

    /// <summary>
    ///     True when normalCount == vertexCount + faceCount, meaning the first VertexCount
    ///     normals are per-vertex (for smooth shading) and the rest are per-face.
    ///     Confirmed by M3dInit_ParsePSX decompilation (stitch flag propagation to per-vertex normals).
    /// </summary>
    public bool HasPerVertexNormals { get; init; }

    /// <summary>Number of vertices in this mesh (needed to index per-vertex normals).</summary>
    public uint VertexCount { get; init; }

    /// <summary>
    ///     Number of type-2 (stitched) vertices whose attachment index could not be resolved.
    ///     Non-zero indicates stitch source ordering mismatch. These vertices are placed at (0,0,0).
    /// </summary>
    public int StitchFailureCount { get; init; }
}

/// <summary>
///     A vertex in a PSX mesh. Coordinates are pre-divided by scale divisor.
/// </summary>
public sealed class PsxVertex
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public ushort Type { get; init; }
    public short RawX { get; init; }
    public short RawY { get; init; }
    public short RawZ { get; init; }
}

/// <summary>
///     A normal vector in a PSX mesh. Pre-divided by 4096.
/// </summary>
public sealed class PsxNormal
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

/// <summary>
///     A face (primitive) in a PSX mesh. Can be a triangle or quad.
/// </summary>
public sealed class PsxFace
{
    public ushort Flags { get; init; }
    public bool IsQuad { get; init; }
    public bool IsTextured { get; init; }
    public bool IsGouraud { get; init; }
    public bool IsSemiTransparent { get; init; }
    public uint Index0 { get; init; }
    public uint Index1 { get; init; }
    public uint Index2 { get; init; }
    public uint Index3 { get; init; }
    public uint NormalIndex { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public byte Mode { get; init; }
    public uint TextureHash { get; init; }
    public byte U0 { get; init; }
    public byte V0 { get; init; }
    public byte U1 { get; init; }
    public byte V1 { get; init; }
    public byte U2 { get; init; }
    public byte V2 { get; init; }
    public byte U3 { get; init; }
    public byte V3 { get; init; }
}
