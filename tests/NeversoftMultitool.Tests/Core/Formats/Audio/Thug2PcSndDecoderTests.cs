using System.Security.Cryptography;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public class Thug2PcSndDecoderTests
{
    private const string WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    private readonly TestPaths _paths = new();

    [Fact]
    public void Decode_MatchesOriginalExecutableOracle()
    {
        // Captured by executing THUG2.exe's decoder at VA 0x005F5A20.
        var payload = Convert.FromHexString("00178F245AE3C6B97DFF");
        short[] expected =
            [0, 0, 30, 34, -23, -26, 12, 30, 13, 69, 100, -7, 184, 24, -24, -126, -363, 332, -1160];

        Assert.Equal(expected, Thug2PcSndCodec.Decode(payload, expected.Length));
    }

    [Fact]
    public void Decode_UsesStepAfterIndexUpdate()
    {
        // Magnitude 4 moves index 0 -> 2 before selecting step 9, producing 10.
        // Textbook IMA would use step 7 first and produce 7 instead.
        Assert.Equal([10], Thug2PcSndCodec.Decode([0x04], 1));
    }

    [Fact]
    public void Decode_TruncatesDifferenceTermsSeparately()
    {
        // step=7, magnitude=1: (7>>2)+(7>>3) = 1. Combining the terms as
        // ((2*1+1)*7)>>3 would incorrectly produce 2.
        Assert.Equal([1], Thug2PcSndCodec.Decode([0x01], 1));
    }

    [Fact]
    public void Decode_OddSampleCountIgnoresFinalHighNibble()
    {
        Assert.Equal(
            Thug2PcSndCodec.Decode([0x01], 1),
            Thug2PcSndCodec.Decode([0xF1], 1));
    }

    [Fact]
    public void Decode_StressVectorMatchesOriginalExecutableHash()
    {
        var ramp = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var payload = ramp.Concat(ramp).Concat(ramp).Concat(new byte[] { 0x08, 0x7F, 0xF0 }).ToArray();

        var pcm = Thug2PcSndCodec.Decode(payload, 1541);

        Assert.Equal(
            "494260dbc6dab888ac14ac5ccdbf1a631fe704f53b90ea7bbdc3a2d991fd7f6a",
            HashPcm(pcm));
    }

    [Fact]
    public void Decode_SampleCountBeyondPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() => Thug2PcSndCodec.Decode([0x00], 3));
    }

    [Fact]
    public void Probe_ValidContainerReportsDecodedDuration()
    {
        var data = BuildSnd([0x01, 0x24], sampleRate: 22050, sampleCount: 3);

        var probe = Thug2PcSndDecoder.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(22050, probe.SampleRate);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(3 / 22050.0, probe.DurationSeconds, 12);
    }

    [Fact]
    public void Probe_OrdinaryPcmHeaderWithSndExtension_IsRejected()
    {
        // A normal PCM WAV stores a byte rate here. THUG2 stores total decoded
        // bytes, whose exact relationship to payload length is the format's tell.
        var data = BuildSnd([0x00, 0x00], avgBytesPerSec: 44100 * 2);

        Assert.Null(Thug2PcSndDecoder.Probe(data));
    }

    [Fact]
    public void Probe_QuarterSecondMonoPcmCollision_IsRejected()
    {
        // For an ordinary 0.25-second mono PCM16 WAV, byteRate == 4 * dataLength.
        // The codec geometry alone therefore collides exactly with THUG2 SND;
        // its honest RIFF length must keep it out of the custom decoder.
        const int sampleRate = 44100;
        var pcm = new byte[sampleRate / 4 * sizeof(short)];
        var data = BuildPcmWave(pcm, sampleRate);

        Assert.Null(Thug2PcSndDecoder.Probe(data));
    }

    [Fact]
    public void ConvertToWav_WritesPlainMonoPcmAndTrimsOddNibble()
    {
        var data = BuildSnd([0x01, 0xF4], sampleRate: 44100, sampleCount: 3);
        Assert.SkipWhen(_paths.TestOutputDir is null, "TestOutput directory not found");
        var outputDir = Path.Combine(_paths.TestOutputDir!, "snd-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = Thug2PcSndDecoder.ConvertToWav(data, "probe", outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);

            var wav = File.ReadAllBytes(Path.Combine(outputDir, "probe.wav"));
            Assert.True(RiffWaveReader.TryRead(wav, out var info));
            Assert.Equal(1, info.FormatTag);
            Assert.Equal(1, info.Channels);
            Assert.Equal(44100, info.SampleRate);
            Assert.Equal(3 * sizeof(short), info.DataLength);

            var expected = Thug2PcSndCodec.Decode([0x01, 0x04], 3);
            Assert.Equal(HashPcm(expected), Convert.ToHexString(SHA256.HashData(
                wav.AsSpan(info.DataOffset, info.DataLength))).ToLowerInvariant());
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ConvertToWav_InvalidDecodedSizeFailsWithoutOutput()
    {
        var data = BuildSnd([0x00, 0x00], avgBytesPerSec: 12);
        Assert.SkipWhen(_paths.TestOutputDir is null, "TestOutput directory not found");
        var outputDir = Path.Combine(_paths.TestOutputDir!, "snd-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = Thug2PcSndDecoder.ConvertToWav(data, "bad", outputDir);

            Assert.False(result.Success);
            Assert.Contains("decoded byte count", result.ErrorMessage);
            Assert.False(File.Exists(Path.Combine(outputDir, "bad.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    ///     Golden raw-PCM hashes from the recovered executable routine. The cases
    ///     cover every corpus sample rate, even and odd sample counts, and all four
    ///     observed RIFF chunk layouts.
    /// </summary>
    [CorpusTheory]
    [InlineData("SloppyLanding.snd", 11025, 16185,
        "5d574f299b37a98bc52c776e64b37af76f23920e5a663c3086664283feca53d5")]
    [InlineData("GrindWireSpark.snd", 22050, 46397,
        "60f0342123d3787e6ed58eaff74782a0c467c192f1c1bf2f47e6b9a536c350d6")]
    [InlineData("CarBrakeSqueal.snd", 44100, 30542,
        "323ce48f5c7ed72bb254604110b1ab71216945a3200f696062e003cefbcfa6d3")]
    [InlineData("MB_HiHat_01.snd", 44100, 3856,
        "9ef1b8b4df515b90469e49b18e1b957d501e01e08c6628582f02e32ffb8a4fc6")]
    [InlineData("Bouncy_AluminumCanHit01.snd", 44100, 8787,
        "c957dac9f2742f8afe1fa661c092dff1f0981410770c80f674ada78e30babc30")]
    [InlineData("Bouncy_PlasticHit02.snd", 48000, 11592,
        "a01e6ece92805e9cbf87388b8000c28643ad73ce5179d08f5a78f3ca6edd1c86")]
    public void Decode_RealFixtureMatchesExecutableOracle(
        string fileName,
        int sampleRate,
        int sampleCount,
        string expectedHash)
    {
        var path = _paths.FindSampleFile(WindowsBuild, fileName);
        Assert.SkipWhen(path is null, $"{fileName} not present in Sample/Builds");

        var data = File.ReadAllBytes(path!);
        Assert.True(RiffWaveReader.TryRead(data, out var info));
        Assert.Equal(sampleRate, info.SampleRate);
        Assert.Equal(sampleCount * sizeof(short), info.AvgBytesPerSec);

        var pcm = Thug2PcSndCodec.Decode(data.AsSpan(info.DataOffset, info.DataLength), sampleCount);

        Assert.Equal(sampleCount, pcm.Length);
        Assert.Equal(expectedHash, HashPcm(pcm));
    }

    [CorpusFact]
    public void Decode_AllPcSndFilesMatchDeclaredGeometry()
    {
        var files = _paths.FindSampleFiles(WindowsBuild, "*.snd").ToList();
        Assert.SkipWhen(files.Count == 0, "No .snd files in Sample/Builds");

        long totalSamples = 0;
        var errors = new List<string>();
        foreach (var path in files)
        {
            var data = File.ReadAllBytes(path);
            if (!RiffWaveReader.TryRead(data, out var info))
            {
                errors.Add($"{Path.GetFileName(path)}: unreadable RIFF/WAVE");
                continue;
            }

            var sampleCount = info.AvgBytesPerSec >> 1;
            try
            {
                var pcm = Thug2PcSndCodec.Decode(data.AsSpan(info.DataOffset, info.DataLength), sampleCount);
                totalSamples += pcm.Length;
                if (Thug2PcSndDecoder.Probe(data) == null)
                    errors.Add($"{Path.GetFileName(path)}: decoder probe rejected valid corpus file");
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
        Assert.Equal(58_372_119, totalSamples);
    }

    private static string HashPcm(short[] pcm)
    {
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static byte[] BuildSnd(
        byte[] payload,
        int sampleRate = 44100,
        int? sampleCount = null,
        int? avgBytesPerSec = null)
    {
        var decodedSamples = sampleCount ?? payload.Length * 2;
        var decodedBytes = avgBytesPerSec ?? decodedSamples * sizeof(short);

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, true))
        {
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(decodedBytes);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(payload.Length);
            writer.Write(payload);
            if ((payload.Length & 1) != 0)
                writer.Write((byte)0);
        }

        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8);
            writer.Write(decodedBytes + 36); // THUG2 stores the decoded size here too.
            writer.Write("WAVE"u8);
            writer.Write(body.ToArray());
        }

        return file.ToArray();
    }

    private static byte[] BuildPcmWave(byte[] pcm, int sampleRate)
    {
        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8);
        writer.Write(pcm.Length + 36);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return file.ToArray();
    }
}
