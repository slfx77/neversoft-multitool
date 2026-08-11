using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     Structural document for one GameCube collision file (.col.ngc).
///     Metadata-only: the format stores topology, flags, terrain, and BSP
///     trees, while vertex positions are sourced from the render scene's
///     vertex pool at runtime, so no geometry can be reconstructed from this
///     file alone.
/// </summary>
public sealed class NgcColScene
{
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

    /// <summary>
    ///     Raw per-face-corner intensity region (3 bytes per face). Uniform
    ///     0xFF (full intensity) in most files; some files carry varied
    ///     authored data.
    /// </summary>
    public required byte[] CornerIntensities { get; init; }

    /// <summary>True when every corner-intensity byte is 0xFF.</summary>
    public required bool CornerIntensitiesUniform { get; init; }

    /// <summary>
    ///     True when every face's vertex indices stay inside its owning
    ///     object's cumulative vertex range; false for the compacted global
    ///     numbering some files use.
    /// </summary>
    public required bool FaceIndicesObjectContained { get; init; }
}
