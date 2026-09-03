using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     Structural document for one GameCube collision file (.col.ngc).
///     The format stores topology, flags, terrain, and BSP trees, while vertex
///     positions are sourced from the render scene's pool at runtime. Geometry
///     conversion is therefore possible only through a separately proven
///     owner/pool binding; this document never invents missing coordinates.
/// </summary>
public sealed class NgcColScene
{
    /// <summary>Exact serialized byte count of the source file.</summary>
    public required int SerializedSize { get; init; }

    /// <summary>SHA-256 of the complete serialized file, uppercase hexadecimal.</summary>
    public required string SerializedSha256 { get; init; }

    /// <summary>Always 10 in the shipped corpus.</summary>
    public required int Version { get; init; }

    /// <summary>
    ///     Supersector grid rows declared in the header. Metadata only — the
    ///     file carries no per-cell table; the engine builds the grid at
    ///     runtime.
    /// </summary>
    public required int SuperSectorRows { get; init; }

    public required int SuperSectorCols { get; init; }

    public required Vector4 SceneBoundsMin { get; init; }

    public required Vector4 SceneBoundsMax { get; init; }

    public required NgcColObject[] Objects { get; init; }

    public required int TotalVerts { get; init; }

    public required int TotalFaces { get; init; }

    /// <summary>Total u16 elements in the trailing BSP leaf face-index pool.</summary>
    public required int PoolElementCount { get; init; }

    /// <summary>Serialized BSP node-array byte count.</summary>
    public required int BspNodeByteCount { get; init; }

    /// <summary>SHA-256 of the serialized BSP node array.</summary>
    public required string BspNodeSha256 { get; init; }

    /// <summary>SHA-256 of the serialized u16 BSP face-index pool.</summary>
    public required string FaceIndexPoolSha256 { get; init; }

    /// <summary>
    ///     Raw per-face-corner intensity region (3 bytes per face). Uniform
    ///     0xFF (full intensity) in most files; some files carry varied
    ///     authored data.
    /// </summary>
    public required byte[] CornerIntensities { get; init; }

    /// <summary>True when every corner-intensity byte is 0xFF.</summary>
    public required bool CornerIntensitiesUniform { get; init; }

    /// <summary>SHA-256 of the serialized three-bytes-per-face intensity region.</summary>
    public required string CornerIntensitiesSha256 { get; init; }

    /// <summary>
    ///     True when every face's vertex indices stay inside its owning
    ///     object's inferred cumulative declared vertex range; false for the
    ///     cross-object/global numbering some canonical files use.
    /// </summary>
    public required bool FaceIndicesWithinCumulativeDeclaredVertexRanges { get; init; }
}
