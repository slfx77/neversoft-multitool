namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsMismatchRegion
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteError { get; init; }
}
