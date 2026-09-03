using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class HandheldAudioFormatSupportTests
{
    [Theory]
    [InlineData(".swav", "SWAV")]
    [InlineData(".strm", "STRM")]
    [InlineData(".hwas", "HWAS")]
    [InlineData(".at3", "AT3")]
    public void Detection_RecognizesDsAndPspFormats(string extension, string expectedFormat)
    {
        Assert.Contains(extension, HandheldAudioFormatSupport.Extensions);
        Assert.Equal(expectedFormat, HandheldAudioFormatSupport.DetectFormat(extension));
    }

    [Theory]
    [InlineData("SWAV")]
    [InlineData("STRM")]
    [InlineData("HWAS")]
    public void ConvertToWav_DsFormats_UseNativeWaveDecoder(string audioFormat)
    {
        using var temp = new TempDirectory();

        var result = HandheldAudioFormatSupport.ConvertToWav(
            audioFormat, BuildSwav(), "converted", temp.Path);

        Assert.NotNull(result);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(File.Exists(Path.Combine(temp.Path, "converted.wav")));
    }

    [Fact]
    public void ConvertToWav_Swav_WritesPlayableWave()
    {
        using var temp = new TempDirectory();

        var result = HandheldAudioFormatSupport.ConvertToWav(
            "SWAV", BuildSwav(), "preview", temp.Path);
        var output = Path.Combine(temp.Path, "preview.wav");

        Assert.NotNull(result);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(output));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output)[..4]));
    }

    [Fact]
    public void ConvertToWav_At3_DispatchesToAt3ContainerGate()
    {
        using var temp = new TempDirectory();

        var result = HandheldAudioFormatSupport.ConvertToWav(
            "AT3", new byte[64], "converted", temp.Path);

        Assert.NotNull(result);
        Assert.True(result.Skipped);
        Assert.Contains("ATRAC3", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToWav_OtherFormat_IsNotClaimed()
    {
        Assert.Null(HandheldAudioFormatSupport.ConvertToWav(
            "ADX", new byte[64], "converted", Path.GetTempPath()));
    }

    [Theory]
    [InlineData("SWAV")]
    [InlineData("STRM")]
    [InlineData("HWAS")]
    public void DurationProbe_DsFormats_UsesContentProbe(string audioFormat)
    {
        var duration = AudioDurationProbe.Probe(audioFormat, BuildSwav());

        Assert.NotNull(duration);
        Assert.True(duration > 0);
    }

    [Fact]
    public void ProbeAudio_Hwas_ReportsTheCustomDsFormatTruthfully()
    {
        var path = FormatProbeTestHelper.CreateTempFile(".hwas", BuildHwas());
        try
        {
            var result = FormatProbe.ProbeAudio(path);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Neversoft DS HWAS", result.FormatName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Minimal valid Nitro SWAV containing one silent ADPCM payload.</summary>
    private static byte[] BuildSwav()
    {
        const int payloadBytes = 32;
        const int dataBlockSize = 8 + 12 + payloadBytes;
        var data = new byte[16 + dataBlockSize];
        "SWAV"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 0x0100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 1);
        "DATA"u8.CopyTo(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), dataBlockSize);
        data[24] = 2; // Nintendo IMA-ADPCM
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 22050);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 760);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), payloadBytes / 4);
        return data;
    }

    /// <summary>Minimal valid custom HWAS stream containing one padded byte.</summary>
    private static byte[] BuildHwas()
    {
        var data = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x68776173);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 16384);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 22019);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), 512);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 1);
        data[512] = 0x07;
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-handheld-audio-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
