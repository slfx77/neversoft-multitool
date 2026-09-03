using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class StandardAudioFormatSupportTests
{
    private static readonly byte[] AsfHeaderObjectGuid =
        [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] AsfDataObjectGuid =
        [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static readonly byte[] AsfFilePropertiesObjectGuid =
        [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    private static readonly byte[] AsfStreamPropertiesObjectGuid =
        [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

    private static readonly byte[] AsfAudioMediaGuid =
        [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

    private static readonly byte[] AsfVideoMediaGuid =
        [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

    [Theory]
    [InlineData(".wav", "WAV")]
    [InlineData(".WAV", "WAV")]
    [InlineData(".wma", "WMA")]
    [InlineData(".WmA", "WMA")]
    public void Detection_RecognizesStandardAudioExtensions(string extension, string expectedFormat)
    {
        Assert.Contains(extension.ToLowerInvariant(), StandardAudioFormatSupport.Extensions);
        Assert.Equal(expectedFormat, StandardAudioFormatSupport.DetectFormat(extension));
    }

    [Fact]
    public void ProbeWave_RequiresACompletePlayableContainerAndReportsDuration()
    {
        var wave = BuildWave();
        var probe = StandardAudioFormatSupport.ProbeWave(wave);

        Assert.NotNull(probe);
        Assert.NotNull(probe.Value.DurationSeconds);
        Assert.Equal(1d, probe.Value.DurationSeconds.Value, precision: 9);

        BinaryPrimitives.WriteUInt32LittleEndian(
            wave.AsSpan(40, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4)) + 1);
        Assert.Null(StandardAudioFormatSupport.ProbeWave(wave));

        wave = BuildWave();
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), 4);
        Assert.Null(StandardAudioFormatSupport.ProbeWave(wave));
    }

    [Fact]
    public void ConvertToWav_WavePassesThroughLosslesslyWithoutFfmpeg()
    {
        using var temp = new TempDirectory();
        var wave = BuildWave();

        var result = StandardAudioFormatSupport.ConvertToWav("WAV", wave, "preview", temp.Path);
        var outputPath = Path.Combine(temp.Path, "preview.wav");

        Assert.NotNull(result);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.Equal(wave, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void ConvertToWav_MalformedWaveIsSkippedWithoutAnOutput()
    {
        using var temp = new TempDirectory();

        var result = StandardAudioFormatSupport.ConvertToWav(
            "WAV", "RIFF-invalid-WAVE"u8.ToArray(), "bad", temp.Path);

        Assert.NotNull(result);
        Assert.True(result.Skipped);
        Assert.False(File.Exists(Path.Combine(temp.Path, "bad.wav")));
    }

    [Fact]
    public void ProbeWindowsMediaAudio_DistinguishesAudioFromVideoOnlyAsf()
    {
        var probe = StandardAudioFormatSupport.ProbeWindowsMediaAudio(BuildAsf(audioStream: true));

        Assert.NotNull(probe);
        Assert.NotNull(probe.Value.DurationSeconds);
        Assert.Equal(2d, probe.Value.DurationSeconds.Value, precision: 9);
        Assert.Null(StandardAudioFormatSupport.ProbeWindowsMediaAudio(BuildAsf(audioStream: false)));
        Assert.Null(StandardAudioFormatSupport.ProbeWindowsMediaAudio(AsfHeaderObjectGuid));
    }

    [Fact]
    public void AudioDurationProbe_DispatchesStandardWaveAndWma()
    {
        Assert.Equal(1d, AudioDurationProbe.Probe("WAV", BuildWave())!.Value, precision: 9);
        Assert.Equal(
            2d,
            AudioDurationProbe.Probe("WMA", BuildAsf(audioStream: true))!.Value,
            precision: 9);
    }

    [Fact]
    public void ConvertWindowsMediaToWav_StagesBytesAndUsesInjectedTranscoderForBatchAndPreviewRoute()
    {
        using var temp = new TempDirectory();
        var wma = BuildAsf(audioStream: true);
        string? stagedInput = null;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            stagedInput = inputPath;
            Assert.EndsWith(".wma", inputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(wma, File.ReadAllBytes(inputPath));
            File.WriteAllBytes(outputPath, BuildWave());
            error = "";
            return true;
        }

        var result = StandardAudioFormatSupport.ConvertWindowsMediaToWav(
            wma, "converted", temp.Path, Transcode);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(File.Exists(Path.Combine(temp.Path, "converted.wav")));
        Assert.NotNull(stagedInput);
        Assert.False(File.Exists(stagedInput));
    }

    [Fact]
    public void ConvertWindowsMediaToWav_RejectsVideoOnlyAsfBeforeCallingTranscoder()
    {
        using var temp = new TempDirectory();
        var called = false;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            called = true;
            error = "should not run";
            return false;
        }

        var result = StandardAudioFormatSupport.ConvertWindowsMediaToWav(
            BuildAsf(audioStream: false), "video", temp.Path, Transcode);

        Assert.True(result.Skipped);
        Assert.False(called);
        Assert.False(File.Exists(Path.Combine(temp.Path, "video.wav")));
    }

    [Fact]
    public void ConvertWindowsMediaToWav_FailurePreservesExistingDestinationAndCleansPartialOutput()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "existing.wav");
        var original = BuildWave();
        File.WriteAllBytes(destination, original);

        bool FailAfterPartialWrite(string inputPath, string outputPath, out string error)
        {
            File.WriteAllBytes(outputPath, "partial"u8.ToArray());
            error = "decode failed";
            return false;
        }

        var result = StandardAudioFormatSupport.ConvertWindowsMediaToWav(
            BuildAsf(audioStream: true), "existing", temp.Path, FailAfterPartialWrite);

        Assert.False(result.Success);
        Assert.Equal("decode failed", result.ErrorMessage);
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(destination, Assert.Single(Directory.GetFiles(temp.Path)));
    }

    [Theory]
    [InlineData(".WaV", true, "WAV Audio")]
    [InlineData(".WmA", false, "Windows Media Audio")]
    public void FormatProbe_StandardAudioIsContentGated(
        string extension,
        bool wave,
        string expectedFormatName)
    {
        var path = FormatProbeTestHelper.CreateTempFile(
            extension,
            wave ? BuildWave() : BuildAsf(audioStream: true));
        try
        {
            var result = FormatProbe.ProbeAudio(path);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal(expectedFormatName, result.FormatName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".wav", true)]
    [InlineData(".wma", false)]
    public void FormatProbe_MislabeledStandardAudioIsUnsupported(string extension, bool wave)
    {
        var path = FormatProbeTestHelper.CreateTempFile(
            extension,
            wave ? "RIFF-invalid-WAVE"u8.ToArray() : BuildAsf(audioStream: false));
        try
        {
            var result = FormatProbe.ProbeAudio(path);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.NotNull(result.UnsupportedReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildWave()
    {
        const int sampleRate = 8_000;
        const int dataBytes = sampleRate * sizeof(short);
        var data = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), sampleRate * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), dataBytes);
        return data;
    }

    private static byte[] BuildAsf(bool audioStream)
    {
        const int filePropertiesSize = 104;
        const int streamPropertiesSize = 96;
        const int headerSize = 30 + filePropertiesSize + streamPropertiesSize;
        const int dataObjectSize = 54;
        var data = new byte[headerSize + dataObjectSize];
        var fileId = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

        AsfHeaderObjectGuid.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 2);
        data[28] = 0x01;
        data[29] = 0x02;

        var offset = 30;
        AsfFilePropertiesObjectGuid.CopyTo(data, offset);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 16), filePropertiesSize);
        fileId.CopyTo(data, offset + 24);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 40), (ulong)data.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 56), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 64), 30_000_000);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 80), 1_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 92), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 96), 4);

        offset += filePropertiesSize;
        AsfStreamPropertiesObjectGuid.CopyTo(data, offset);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 16), streamPropertiesSize);
        (audioStream ? AsfAudioMediaGuid : AsfVideoMediaGuid).CopyTo(data, offset + 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 64), 18);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 72), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 78), 0x0161);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 80), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 82), 44_100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 86), 8_000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 90), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 92), 16);

        AsfDataObjectGuid.CopyTo(data, headerSize);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(headerSize + 16), dataObjectSize);
        fileId.CopyTo(data, headerSize + 24);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(headerSize + 40), 1);
        data[headerSize + 48] = 0x01;
        data[headerSize + 49] = 0x01;
        data[headerSize + 50] = 0x82;
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-standard-audio-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
