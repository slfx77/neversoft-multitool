using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class WiiDspAudioTests(TestPaths paths)
{
    private const string DownhillJamWiiBuild =
        "Tony Hawk's Downhill Jam (2006-11-19, Wii - Final)";
    private const string ProvingGroundWiiBuild =
        "Tony Hawk's Proving Ground (2007-10-16, Wii - Final)";

    [Fact]
    public void Probe_ValidatesHeaderPayloadAndReportsDuration()
    {
        var data = BuildDsp([-7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6]);

        var probe = WiiDspAudio.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(14, probe.SampleCount);
        Assert.Equal(32_000, probe.SampleRate);
        Assert.False(probe.IsLooping);
        Assert.Equal(14d / 32_000, probe.DurationSeconds, precision: 12);
    }

    [Fact]
    public void Decode_UsesNintendoPredictorScaleMath()
    {
        int[] nibbles = [-7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6];
        var data = BuildDsp(nibbles);

        var samples = WiiDspAudio.Decode(data);

        Assert.Equal(nibbles.Select(static value => (short)(value * 2048)), samples);
    }

    [Fact]
    public void Decode_HonoursPredictorCoefficientsAndInitialHistory()
    {
        var data = BuildDsp(Enumerable.Repeat(0, 14).ToArray());
        // Predictor 1, scale 1; c1=1.0, c2=0.0 in DSP's Q11 domain.
        data[WiiDspAudio.HeaderSize] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x3E, 2), 0x10);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(0x20, 2), 2048);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(0x22, 2), 0);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(0x40, 2), 1234);

        var samples = WiiDspAudio.Decode(data);

        Assert.All(samples, sample => Assert.Equal((short)1234, sample));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Probe_RejectsBrokenIndependentIdentities(int mutation)
    {
        var data = BuildDsp(Enumerable.Repeat(0, 14).ToArray());
        switch (mutation)
        {
            case 1:
                Array.Resize(ref data, data.Length - 1); // Truncated payload.
                break;
            case 2:
                data[0x3F] ^= 1; // Header predictor/scale no longer matches frame zero.
                break;
            case 3:
                data[WiiDspAudio.HeaderSize] = 0x80; // Predictor index 8 is invalid.
                data[0x3F] = 0x80;
                break;
        }

        Assert.Null(WiiDspAudio.Probe(data));
    }

    [Fact]
    public void ConvertToWav_WritesMonoPcmAndPreservesExistingOutputOnInvalidInput()
    {
        using var temp = new TempDirectory();
        var data = BuildDsp(Enumerable.Range(-7, 14).ToArray());

        var result = WiiDspAudio.ConvertToWav(data, "stream", temp.Path);
        var outputPath = Path.Combine(temp.Path, "stream.wav");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(RiffWaveReader.TryRead(File.ReadAllBytes(outputPath), out var wave));
        Assert.Equal(32_000, wave.SampleRate);
        Assert.Equal(1, wave.Channels);

        var original = File.ReadAllBytes(outputPath);
        var invalid = WiiDspAudio.ConvertToWav(data[..^1], "stream", temp.Path);
        Assert.True(invalid.Skipped);
        Assert.Equal(original, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void SharedDispatchDurationAndFormatProbe_RecognizeExtensionlessDsp()
    {
        using var temp = new TempDirectory();
        var data = BuildDsp(Enumerable.Repeat(0, 14).ToArray());
        var filePath = Path.Combine(temp.Path, "fallwater");
        File.WriteAllBytes(filePath, data);

        var converted = HandheldAudioFormatSupport.ConvertToWav("DSP", data, "shared", temp.Path);
        var duration = AudioDurationProbe.Probe("DSP", data);
        var format = FormatProbe.ProbeAudio(filePath);

        Assert.NotNull(converted);
        Assert.True(converted.Success, converted.ErrorMessage);
        Assert.Equal(14d / 32_000, duration!.Value, precision: 12);
        Assert.Equal(FormatProbe.FormatSupport.Supported, format.Support);
        Assert.Equal("Nintendo DSP-ADPCM", format.FormatName);
    }

    [CorpusFact]
    public void Probe_AllWiiExtensionlessStreams_ConsumesTheExactCorpus()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(DownhillJamWiiBuild, "*")
            .Concat(paths.FindSampleFiles(ProvingGroundWiiBuild, "*"))
            .Where(static path => string.IsNullOrEmpty(Path.GetExtension(path)))
            .Where(static path => WiiDspAudio.IsWiiDsp(File.ReadAllBytes(path)))
            .ToArray();

        Assert.Equal(6578, files.Length);
        Assert.All(files, file =>
        {
            var data = File.ReadAllBytes(file);
            var probe = WiiDspAudio.Probe(data);
            Assert.NotNull(probe);
            Assert.Equal(data.Length,
                WiiDspAudio.HeaderSize + ((probe.SampleCount + 13L) / 14) * 8);
        });
    }

    private static byte[] BuildDsp(IReadOnlyList<int> nibbles)
    {
        if (nibbles.Count is <= 0 or > 14)
            throw new ArgumentOutOfRangeException(nameof(nibbles));

        var data = new byte[WiiDspAudio.HeaderSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)nibbles.Count);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 32_000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x0C), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x0E), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x3E), 0x0B);
        data[WiiDspAudio.HeaderSize] = 0x0B;
        for (var index = 0; index < nibbles.Count; index++)
        {
            var nibble = nibbles[index] & 0x0F;
            var byteOffset = WiiDspAudio.HeaderSize + 1 + index / 2;
            if ((index & 1) == 0)
                data[byteOffset] = (byte)(nibble << 4);
            else
                data[byteOffset] |= (byte)nibble;
        }

        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-wii-dsp-{Guid.NewGuid():N}");
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
