namespace NeversoftMultitool.Core.Formats.Trg;

public sealed class TrgPosition
{
    internal int RawX { get; init; }
    internal int RawY { get; init; }
    internal int RawZ { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}
