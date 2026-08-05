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
    ///     Structurally valid faces the loader marks invisible (disc bit7-set
    ///     opaque class — the M3dInit STP toggle turns them collision-only).
    ///     They are NOT part of <see cref="Faces" /> and stay out of every
    ///     count, snapshot, and regular emission path. Retained because the
    ///     engine still draws such meshes when an entity's item flag 0x800
    ///     forces semi-transparency at render time (M3d_Render ORs 0x40|ABR
    ///     into the face flags — l1a2's Watcher apparition).
    /// </summary>
    public IReadOnlyList<PsxFace> InvisibleFaces { get; init; } = [];

    /// <summary>
    ///     The PC/DC v6 loader retains native header bit 2 and also derives it
    ///     when any declared native face carries flag 0x0004 — and the PS1
    ///     loader does the SAME: M3dInit_ParsePSX ORs every face word0 into
    ///     the runtime SModel.Flags, setting bit 2 when any face carries it
    ///     (decomp-VERIFIED, psx_model_load_format.md §5.3; disc mesh headers
    ///     ship 0x8 across lit characters and FE props alike, so the header
    ///     bit alone is meaningless). The renderer keeps that mode only when
    ///     the mesh supplies enough vertex and face normals, then bypasses
    ///     authored face/RGBs colours.
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
            return Normals.Count >= VertexCount + rawFaceCount;
        }
    }

    /// <summary>
    ///     True when any vertex carries the sprite type bit (0x10) — the mesh
    ///     contains axial-billboard corners whose stored fields are anchor/mate
    ///     byte offsets rather than positions (the loader mirrors this as mesh
    ///     Flags bit 0x100 at runtime). Conversion resolves them through
    ///     <see cref="Conversion.PsxSpriteVertexResolver" />.
    /// </summary>
    public bool HasSpriteVertices =>
        Vertices.Exists(static vertex => PsxMeshSemantics.IsSpriteVertex(vertex.Type));

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
