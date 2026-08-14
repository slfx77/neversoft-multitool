using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class XaDecoderTests
{
    private const int SectorSize = 2336;

    [Fact]
    public void ConvertToWav_EmptyInputFailsWithoutCreatingOutput()
    {
        var outputDir = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_XaDecoder_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = XaDecoder.ConvertToWav([], "empty", outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Contains("Unrecognized XA format", result.ErrorMessage);
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void DecodeToSamples_SecondSectorWithMismatchedRepeatedSubheader_ReturnsNull()
    {
        var data = BuildTwoSectorXa();
        data[SectorSize + 6] ^= 0x04;

        Assert.Null(XaDecoder.DecodeToSamples(data));
    }

    [Fact]
    public void DecodeToSamples_TwoValidSilentSectors_DecodesEverySample()
    {
        var data = BuildTwoSectorXa();

        var decoded = AssertDecoded(data);

        Assert.Equal(8_064, decoded.Samples.Length);
        Assert.Equal(37_800, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
    }

    [Fact]
    public void DecodeToSamples_LaterDuplicatedNonAudioSector_IsSkipped()
    {
        var data = BuildTwoSectorXa();
        data[SectorSize + 2] = 0;
        data[SectorSize + 6] = 0;

        var decoded = AssertDecoded(data);

        Assert.Equal(4_032, decoded.Samples.Length);
        Assert.Equal(37_800, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
    }

    [Fact]
    public void DecodeToSamples_FilterMatchingOnlyNonAudioSector_ReturnsNull()
    {
        var data = BuildTwoSectorXa();
        data[1] = 1;
        data[5] = 1;
        data[SectorSize + 1] = 2;
        data[SectorSize + 5] = 2;
        data[SectorSize + 2] = 0;
        data[SectorSize + 6] = 0;

        Assert.Null(XaDecoder.DecodeToSamples(data, 2));

        var audio = Assert.IsType<(short[] Samples, int SampleRate, int Channels)>(
            XaDecoder.DecodeToSamples(data, 1));
        Assert.Equal(4_032, audio.Samples.Length);
    }

    [Fact]
    public void ConvertToWav_NonAudioOnlySecondChannel_WritesOneFlatAudioFile()
    {
        var data = BuildTwoSectorXa();
        data[1] = 1;
        data[5] = 1;
        data[SectorSize + 1] = 2;
        data[SectorSize + 5] = 2;
        data[SectorSize + 2] = 0;
        data[SectorSize + 6] = 0;
        var outputDir = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_XaDecoder_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = XaDecoder.ConvertToWav(data, "mixed", outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);
            var wavPath = Path.Combine(outputDir, "mixed.wav");
            Assert.True(File.Exists(wavPath));
            Assert.Equal(8_108L, new FileInfo(wavPath).Length);
            Assert.False(Directory.Exists(Path.Combine(outputDir, "mixed")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static byte[] BuildTwoSectorXa()
    {
        var data = new byte[2 * SectorSize];
        for (var offset = 0; offset < data.Length; offset += SectorSize)
        {
            data[offset + 2] = 0x04;
            data[offset + 6] = 0x04;
        }

        return data;
    }

    private static (short[] Samples, int SampleRate, int Channels) AssertDecoded(byte[] data)
    {
        return Assert.IsType<(short[] Samples, int SampleRate, int Channels)>(
            XaDecoder.DecodeToSamples(data));
    }
}
