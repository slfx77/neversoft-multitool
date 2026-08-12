using NeversoftMultitool.Core.Formats.GsDump;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsDumpAuditPixelDiffTests
{
    [Fact]
    public void CompareAgainstPixels_FullImageRegionUsesRawDifferences()
    {
        byte[] renderPixels = [255, 255, 255, 255];
        byte[] referencePixels = [0, 0, 0, 255];

        var stats = GsDumpAuditPixelDiff.CompareAgainstPixels(
            renderPixels,
            1,
            1,
            referencePixels,
            1,
            1,
            diffPath: null);

        Assert.Equal(255d, stats.MeanAbsoluteError);
        Assert.Equal(255, stats.MaxChannelDifference);
        var region = Assert.Single(stats.TopMismatchRegions);
        Assert.Equal(255d, region.MeanAbsoluteError);
    }
}
