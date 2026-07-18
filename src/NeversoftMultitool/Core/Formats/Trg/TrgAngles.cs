namespace NeversoftMultitool.Core.Formats.Trg;

public sealed class TrgAngles
{
    internal short RawX { get; init; }
    internal short RawY { get; init; }
    internal short RawZ { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}
