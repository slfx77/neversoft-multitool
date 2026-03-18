namespace NeversoftMultitool.Core.Formats.Psx;

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
