using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class StrMediaSourceSeekAlignmentTests
{
    [Fact]
    public void AlignExplicit_NonFrameBoundary_UsesActualVideoFrameForAudio()
    {
        var aligned = Align(TimeSpan.FromMilliseconds(90), frameCount: 10, audioByteLength: 200_000);

        Assert.Equal(1, aligned.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(1d / 15d), aligned.ActualPosition);
        Assert.Equal(10_080, aligned.AudioByteOffset);
        Assert.NotEqual(13_608, aligned.AudioByteOffset);
        Assert.NotEqual(10_076, aligned.AudioByteOffset);
    }

    [Fact]
    public void AlignExplicit_Zero_StartsBothStreamsAtZero()
    {
        var aligned = Align(TimeSpan.Zero, frameCount: 10, audioByteLength: 200_000);

        Assert.Equal(0, aligned.FrameIndex);
        Assert.Equal(TimeSpan.Zero, aligned.ActualPosition);
        Assert.Equal(0, aligned.AudioByteOffset);
    }

    [Fact]
    public void AlignExplicit_NoAudio_UsesZeroAudioOffset()
    {
        var aligned = Align(TimeSpan.FromMilliseconds(90), frameCount: 10, audioByteLength: 0);

        Assert.Equal(1, aligned.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(1d / 15d), aligned.ActualPosition);
        Assert.Equal(0, aligned.AudioByteOffset);
    }

    [Fact]
    public void AlignExplicit_PastEnd_ClampsToLastVideoFrameAndAlignedAudio()
    {
        var aligned = Align(TimeSpan.FromHours(1), frameCount: 3, audioByteLength: 200_000);

        Assert.Equal(2, aligned.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(2d / 15d), aligned.ActualPosition);
        Assert.Equal(20_160, aligned.AudioByteOffset);
    }

    [Fact]
    public void AlignExplicit_RoundedPcmTimestamp_DoesNotPrecedeReportedStart()
    {
        const double frameRate = 150d / 19d;
        const int sampleRate = 18_900;
        const int channels = 2;
        var requested = TimeSpan.FromSeconds(99d / frameRate + 0.001d);

        var aligned = StrMediaSourceSeekAlignment.AlignExplicit(
            requested,
            frameRate,
            frameCount: 120,
            audioSampleRate: sampleRate,
            audioChannels: channels,
            audioByteLength: 2_000_000);
        var audioTimestamp = TimeSpan.FromSeconds(
            (double)aligned.AudioByteOffset / (sampleRate * channels * sizeof(short)));

        Assert.Equal(99, aligned.FrameIndex);
        Assert.Equal(948_028, aligned.AudioByteOffset);
        Assert.True(audioTimestamp >= aligned.ActualPosition);
    }

    private static StrMediaSourceSeekPosition Align(
        TimeSpan requestedPosition,
        int frameCount,
        int audioByteLength)
    {
        return StrMediaSourceSeekAlignment.AlignExplicit(
            requestedPosition,
            frameRate: 15d,
            frameCount: frameCount,
            audioSampleRate: 37_800,
            audioChannels: 2,
            audioByteLength: audioByteLength);
    }
}
