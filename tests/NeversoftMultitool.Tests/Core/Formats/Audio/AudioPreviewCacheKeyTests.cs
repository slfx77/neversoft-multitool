using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class AudioPreviewCacheKeyTests
{
    [Fact]
    public void SameNamedSources_HaveDistinctPreviewIdentities()
    {
        var first = new PreviewParent("bank.vab");
        var second = new PreviewParent("bank.vab");
        Assert.Equal(first, second);

        var firstSample = new AudioPreviewCacheKey(first, 3, 22050);
        var secondSample = new AudioPreviewCacheKey(second, 3, 22050);

        Assert.NotEqual(firstSample, secondSample);
        Assert.Equal(firstSample, new AudioPreviewCacheKey(first, 3, 22050));
        Assert.NotEqual(firstSample, new AudioPreviewCacheKey(first, null, 22050));
        Assert.NotEqual(firstSample, new AudioPreviewCacheKey(first, 3, 44100));
    }

    private sealed record PreviewParent(string FileName);
}
