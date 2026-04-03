namespace NeversoftMultitool.Core.Formats.Psx;

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
        ? (uint)(ushort)RawY
        : null;
}
