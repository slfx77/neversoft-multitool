using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class AudioDurationProbeTests
{
    private const int XaSectorSize = 2336;
    private const int XaSamplesPerSector = 18 * 8 * 28;

    [Fact]
    public void Probe_RawXaUsesDecoderStereoTimeline()
    {
        var raw = new byte[128 * 3];
        raw[0] = 0x11; // keep it out of the repeated-subheader route

        var duration = AudioDurationProbe.Probe("XA", raw);

        Assert.Equal(3 * 112 / 37800.0, duration);
    }

    [Fact]
    public void Probe_SectoredXaUsesLongestChannelRatherThanSum()
    {
        var data = CreateSectoredXa((1, 0), (2, 0), (1, 0), (1, 0));

        var duration = AudioDurationProbe.Probe("XA", data);

        Assert.Equal(3.0 * XaSamplesPerSector / 37800, duration);
    }

    [Fact]
    public void Probe_MultiTrackVidUsesLongestTrackRatherThanSum()
    {
        var data = Vid1TestBuilder.CreateMultiTrackVid1(
            2,
            static _ => 1000,
            static _ => 1,
            static index => index == 0 ? 250 : 750);

        var duration = AudioDurationProbe.Probe("VID", data);

        Assert.Equal(0.75, duration);
    }

    [Theory]
    [InlineData("VAB")]
    [InlineData("KAT")]
    [InlineData("unknown")]
    public void Probe_FormatsWithoutOneParentTimelineReturnNull(string format)
    {
        Assert.Null(AudioDurationProbe.Probe(format, []));
    }

    [Theory]
    [InlineData(56L, 22050, 56 / 22050.0)]
    [InlineData(null, 22050, null)]
    [InlineData(56L, 0, null)]
    public void FromDecodedFrames_RequiresFramesAndRate(long? frames, int rate, double? expected)
    {
        Assert.Equal(expected, AudioDurationProbe.FromDecodedFrames(frames, rate));
    }

    [Fact]
    public void Probe_InvalidBytesReturnNull()
    {
        Assert.Null(AudioDurationProbe.Probe("ADX", [0x80]));
        Assert.Null(AudioDurationProbe.Probe("PSS", []));
        Assert.Null(AudioDurationProbe.Probe("VID", []));
    }

    private static byte[] CreateSectoredXa(params (int Channel, byte Coding)[] sectors)
    {
        var data = new byte[sectors.Length * XaSectorSize];
        for (var sectorIndex = 0; sectorIndex < sectors.Length; sectorIndex++)
        {
            var offset = sectorIndex * XaSectorSize;
            data[offset + 1] = (byte)sectors[sectorIndex].Channel;
            data[offset + 2] = 0x64;
            data[offset + 3] = sectors[sectorIndex].Coding;
            data.AsSpan(offset, 4).CopyTo(data.AsSpan(offset + 4, 4));
        }

        return data;
    }
}
