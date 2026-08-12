using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class XaExtractorTests(TestPaths paths)
{
    private const int SectorSize = 2336;
    private const int SamplesPerSector = 18 * 8 * 28;

    [Fact]
    public void EnumerateChannels_SyntheticTwoChannelStream_ReportsPerChannelFormat()
    {
        // Interleaved: channel 1 mono 37800 Hz, channel 2 stereo 18900 Hz.
        var data = CreateSectoredXa((1, 0x00), (2, 0x05), (1, 0x00), (2, 0x05));

        var channels = XaExtractor.EnumerateChannels(data);

        Assert.Equal(2, channels.Count);

        var first = channels[0];
        Assert.Equal(1, first.ChannelNumber);
        Assert.Equal(2, first.SectorCount);
        Assert.Equal(37800, first.SampleRate);
        Assert.False(first.IsStereo);
        Assert.Equal(2 * 18 * 128, first.DataSize);
        Assert.Equal(2.0 * SamplesPerSector / 37800, first.DurationSeconds, 6);

        var second = channels[1];
        Assert.Equal(2, second.ChannelNumber);
        Assert.Equal(2, second.SectorCount);
        Assert.Equal(18900, second.SampleRate);
        Assert.True(second.IsStereo);
        Assert.Equal(2.0 * SamplesPerSector / 2 / 18900, second.DurationSeconds, 6);
    }

    [Fact]
    public void EnumerateChannels_SingleChannelStream_ReturnsOneEntry()
    {
        var data = CreateSectoredXa((0, 0x00), (0, 0x00));

        var channels = XaExtractor.EnumerateChannels(data);

        var only = Assert.Single(channels);
        Assert.Equal(0, only.ChannelNumber);
        Assert.Equal(2, only.SectorCount);
    }

    [Fact]
    public void EnumerateChannels_NonSectoredData_ReturnsEmpty()
    {
        // Raw sound-group data has no repeated sub-header.
        var raw = new byte[128 * 4];
        raw[0] = 0x11;

        Assert.Empty(XaExtractor.EnumerateChannels(raw));
    }

    [Fact]
    public void ExtractChannelToWav_DecodesOnlyTheRequestedChannel()
    {
        var data = CreateSectoredXa((1, 0x00), (2, 0x05), (1, 0x00), (2, 0x05));
        var outputDir = FormatProbeTestHelper.CreateTempDirectory("xa_extract");

        try
        {
            var wavPath = XaExtractor.ExtractChannelToWav(data, "test", 2, outputDir);

            Assert.NotNull(wavPath);
            Assert.Equal("test_ch02.wav", Path.GetFileName(wavPath));

            var wavBytes = File.ReadAllBytes(wavPath!);
            Assert.True(wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
            Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2)));
            Assert.Equal(18900, BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4)));

            // Two sectors of channel 2 decode to 2 × 4032 interleaved samples.
            Assert.Equal(44 + 2 * SamplesPerSector * sizeof(short), wavBytes.Length);

            Assert.Null(XaExtractor.ExtractChannelToWav(data, "test", 9, outputDir));
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
    public void EnumerateChannels_RepresentativeSample_FindsMultipleChannels()
    {
        var samplePath = paths.FindSampleFile("Spider-Man (2000-2-4, PSX - Prototype)", "VOICE00.XA");
        Assert.SkipWhen(samplePath == null, "Representative PS1 XA sample not found");

        var channels = XaExtractor.EnumerateChannels(File.ReadAllBytes(samplePath!));

        Assert.True(channels.Count > 1);
        Assert.All(channels, channel =>
        {
            Assert.True(channel.SectorCount > 0);
            Assert.True(channel.SampleRate is 18900 or 37800);
            Assert.True(channel.DurationSeconds > 0);
        });
    }

    /// <summary>
    ///     Builds a minimal sectored XA stream: per sector an 8-byte sub-header
    ///     (file, channel, submode, coding — repeated) over silent sound groups.
    /// </summary>
    private static byte[] CreateSectoredXa(params (int Channel, byte Coding)[] sectors)
    {
        var data = new byte[sectors.Length * SectorSize];
        for (var s = 0; s < sectors.Length; s++)
        {
            var offset = s * SectorSize;
            data[offset + 1] = (byte)sectors[s].Channel;
            data[offset + 2] = 0x64; // submode: real-time + audio (bit 2) + channel-end
            data[offset + 3] = sectors[s].Coding;
            data[offset + 4] = data[offset];
            data[offset + 5] = data[offset + 1];
            data[offset + 6] = data[offset + 2];
            data[offset + 7] = data[offset + 3];
        }

        return data;
    }
}
