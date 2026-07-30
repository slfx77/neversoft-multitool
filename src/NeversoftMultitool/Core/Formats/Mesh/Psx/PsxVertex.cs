namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     A vertex in a PSX mesh. Coordinates are pre-divided by scale divisor.
/// </summary>
public sealed class PsxVertex
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public ushort Type { get; init; }
    public short RawX { get; init; }
    public short RawY { get; init; }
    public short RawZ { get; init; }

    internal uint? AttachmentIndex { get; set; }

    internal uint? AttachmentTargetIndex => PsxMeshSemantics.IsExactStitchedReference(Type)
        ? (ushort)RawY
        : null;

    /// <summary>
    ///     Type bit4: a screen-expanded sprite corner whose stored fields are
    ///     NOT a position — see <see cref="PsxMeshSemantics.SpriteVertexTypeBit" />.
    ///     For such a vertex <see cref="X" />/<see cref="Y" /> hold the raw
    ///     byte offsets divided by the scale divisor (garbage as coordinates);
    ///     only <see cref="Z" /> is meaningful (the signed perpendicular
    ///     half-width, already in model units).
    /// </summary>
    public bool IsSpriteVertex => PsxMeshSemantics.IsSpriteVertex(Type);

    /// <summary>Anchor vertex index (raw X is a byte offset, index × 8).</summary>
    internal int SpriteAnchorVertexIndex => (ushort)RawX / 8;

    /// <summary>Axis-mate vertex index (raw Y is a byte offset, index × 8).</summary>
    internal int SpriteAxisMateVertexIndex => (ushort)RawY / 8;
}
