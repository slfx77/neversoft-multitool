namespace NeversoftMultitool.Core.Formats.Psx;

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

    internal IReadOnlyList<PsxFaceReadInfo> FaceReadInfos { get; init; } = [];
}
