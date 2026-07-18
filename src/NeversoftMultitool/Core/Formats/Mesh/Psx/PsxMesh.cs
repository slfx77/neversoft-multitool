namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A parsed mesh within a PSX file. Contains vertices, normals, and faces.
/// </summary>
public sealed class PsxMesh
{
    /// <summary>Native mesh-header flags retained by the PC/DC loader.</summary>
    public uint Flags { get; init; }

    public required List<PsxVertex> Vertices { get; init; }
    public required List<PsxNormal> Normals { get; init; }
    public required List<PsxFace> Faces { get; init; }

    /// <summary>
    ///     The PC/DC v6 loader retains native header bit 2 and also derives it
    ///     when any declared native face carries flag 0x0004. The renderer
    ///     keeps that mode only when the mesh supplies enough vertex and face
    ///     normals, then bypasses authored face/RGBs colours.
    /// </summary>
    public bool UsesDynamicLighting
    {
        get
        {
            var hasLightingFlag = (Flags & 0x0004) != 0 ||
                                  (FaceReadInfos.Count > 0
                                      ? FaceReadInfos.Any(static face => (face.Flags & 0x0004) != 0)
                                      : Faces.Exists(static face => (face.Flags & 0x0004) != 0));
            if (!hasLightingFlag)
                return false;

            var rawFaceCount = FaceReadInfos.Count > 0
                ? FaceReadInfos.Count
                : Faces.Count;
            return Normals.Count >= (long)VertexCount + rawFaceCount;
        }
    }

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
