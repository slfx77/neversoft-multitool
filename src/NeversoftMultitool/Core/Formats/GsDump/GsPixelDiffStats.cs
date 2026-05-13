namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsPixelDiffStats
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double MeanAbsoluteError { get; init; }
    public double RootMeanSquareError { get; init; }
    public int MaxChannelDifference { get; init; }
    public GsPixelBounds? RenderBounds { get; init; }
    public GsPixelBounds? ReferenceBounds { get; init; }
    public List<GsMismatchRegion> TopMismatchRegions { get; init; } = [];
}
