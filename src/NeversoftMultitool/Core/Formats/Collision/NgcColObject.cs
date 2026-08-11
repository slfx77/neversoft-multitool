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

    /// <summary>Declared vertex count (the positions live in the scene pool).</summary>
    public required int NumVerts { get; init; }

    public required Vector4 BBoxMin { get; init; }

    public required Vector4 BBoxMax { get; init; }

    /// <summary>Cumulative vertex base of this object in the file's global numbering.</summary>
    public required int FirstVertIndex { get; init; }

    /// <summary>Cumulative face base of this object in the file's face array.</summary>
    public required int FirstFaceIndex { get; init; }

    public required NgcColFace[] Faces { get; init; }

    /// <summary>Root of this object's axis-aligned BSP tree.</summary>
    public required NgcColBspNode BspRoot { get; init; }
}
