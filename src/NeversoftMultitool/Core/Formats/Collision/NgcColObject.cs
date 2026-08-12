using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     One 64-byte big-endian GameCube collision object record plus the face
///     and BSP data it owns. Vertex positions are not stored in the file —
///     the engine binds them to the render scene's vertex pool at load
///     (THUG source <c>NxScene.cpp read_collision</c> /
///     <c>CCollObjTriData::InitCollObjTriData</c>, <c>__PLAT_NGC__</c>).
/// </summary>
public sealed class NgcColObject
{
    public required uint Checksum { get; init; }

    /// <summary>Serialized object flags at +0x04.</summary>
    public required ushort Flags { get; init; }

    /// <summary>Declared vertex count (the positions live in the scene pool).</summary>
    public required int NumVerts { get; init; }

    public required Vector4 BBoxMin { get; init; }

    public required Vector4 BBoxMax { get; init; }

    /// <summary>
    ///     Cumulative base inferred from preceding declared vertex counts. This
    ///     is inspection metadata, not a serialized object-local ownership
    ///     boundary: canonical files exist whose global face indices cross it.
    /// </summary>
    public required int CumulativeDeclaredVertexBase { get; init; }

    /// <summary>Cumulative face base of this object in the file's face array.</summary>
    public required int FirstFaceIndex { get; init; }

    /// <summary>Serialized small-face selector. False in the canonical THAW GC corpus.</summary>
    public required bool UsesSmallFaces { get; init; }

    /// <summary>Serialized fixed-vertex selector. False in the canonical THAW GC corpus.</summary>
    public required bool UsesFixedVertices { get; init; }

    /// <summary>Byte offset of this object's root in the shared BSP node array.</summary>
    public required int BspNodeByteOffset { get; init; }

    /// <summary>Byte offset into the shared three-bytes-per-face intensity region.</summary>
    public required int CornerIntensityByteOffset { get; init; }

    public required NgcColFace[] Faces { get; init; }

    /// <summary>Root of this object's axis-aligned BSP tree.</summary>
    public required NgcColBspNode BspRoot { get; init; }
}
