using System.Security.Cryptography;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public class XboxPcmDecoderTests
{
    private const string XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    private readonly TestPaths _paths = new();

    /// <summary>Builds one 36-byte Xbox ADPCM block.</summary>
    private static byte[] Block(short predictor, byte stepIndex, byte payloadByte)
    {
        var block = new byte[XboxImaAdpcm.BlockAlignPerChannel];
        BitConverter.GetBytes(predictor).CopyTo(block, 0);
        block[2] = stepIndex;
        for (var i = 4; i < block.Length; i++)
            block[i] = payloadByte;
        return block;
    }

    private static byte[] Wave(int blockAlign, int dataLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + 20 + 8 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(20);
        writer.Write((ushort)XboxImaAdpcm.FormatTag);
        writer.Write((ushort)1);
        writer.Write(44100);
        writer.Write(44100 * XboxImaAdpcm.BlockAlignPerChannel / XboxImaAdpcm.SamplesPerBlock);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)4);
        writer.Write((ushort)2);
        writer.Write((ushort)XboxImaAdpcm.SamplesPerBlock);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }

    [Fact]
    public void Decode_EmitsHeaderPredictorThenSixtyThreeSamples()
    {
        var samples = XboxImaAdpcm.Decode(Block(1234, 0, 0x00), 1);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock, samples.Length);
        Assert.Equal(1234, samples[0]);
    }

    [Fact]
    public void Decode_DiscardsTheSixtyFourthNibble()
    {
        // 64 nibbles are present but only 63 are consumed. Craft a block whose
        // LAST nibble would move the predictor hard, and prove it never lands:
        // decoding a block that differs only in that nibble gives the same output.
        var a = Block(0, 0, 0x00);
        var b = Block(0, 0, 0x00);
        b[^1] = 0xF0; // high nibble of the final byte = the discarded 64th

        Assert.Equal(XboxImaAdpcm.Decode(a, 1), XboxImaAdpcm.Decode(b, 1));
    }

    [Fact]
    public void Decode_StepIndexSaturatesAtTableBounds()
    {
        // 0x77 is nibble 7 twice — index += 8 every step, which would run off the
        // 89-entry table without clamping.
        var samples = XboxImaAdpcm.Decode(Block(0, 88, 0x77), 1);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock, samples.Length);
    }

    [Fact]
    public void Decode_PredictorClampsToInt16Range()
    {
        var samples = XboxImaAdpcm.Decode(Block(short.MaxValue, 88, 0x77), 1);

        Assert.All(samples, s => Assert.InRange(s, short.MinValue, short.MaxValue));
    }

    [Fact]
    public void Decode_Stereo_InterleavesPerChannelSubBlocks()
    {
        // Two 36-byte sub-blocks: channel 0 starts at +1000, channel 1 at -1000.
        var stereo = new byte[XboxImaAdpcm.BlockAlignPerChannel * 2];
        Block(1000, 0, 0x00).CopyTo(stereo, 0);
        Block(-1000, 0, 0x00).CopyTo(stereo, XboxImaAdpcm.BlockAlignPerChannel);

        var samples = XboxImaAdpcm.Decode(stereo, 2);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock * 2, samples.Length);
        Assert.Equal(1000, samples[0]); // frame 0, channel 0
        Assert.Equal(-1000, samples[1]); // frame 0, channel 1
    }

    [Fact]
    public void Decode_TrailingPartialBlock_IsIgnored()
    {
        var data = new byte[XboxImaAdpcm.BlockAlignPerChannel + 10];
        Block(7, 0, 0x00).CopyTo(data, 0);

        Assert.Equal(XboxImaAdpcm.SamplesPerBlock, XboxImaAdpcm.Decode(data, 1).Length);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsNoSamples()
    {
        Assert.Empty(XboxImaAdpcm.Decode([], 1));
    }

    [Theory]
    [InlineData(35, 36)]
    [InlineData(36, 35)]
    public void Probe_RejectsGeometryThatConversionCannotDecode(int blockAlign, int dataLength)
    {
        Assert.Null(XboxPcmDecoder.Probe(Wave(blockAlign, dataLength)));
    }

    [Fact]
    public void Probe_AcceptsOneCompleteMonoBlock()
    {
        var data = Wave(
            XboxImaAdpcm.BlockAlignPerChannel,
            XboxImaAdpcm.BlockAlignPerChannel);
        var probe = XboxPcmDecoder.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(44100, probe!.SampleRate);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(XboxImaAdpcm.SamplesPerBlock / 44100.0, probe.DurationSeconds);
        Assert.Equal(probe.DurationSeconds, AudioDurationProbe.Probe("PCM", data));
    }

    /// <summary>
    ///     Golden decodes verified against ffmpeg's dedicated <c>adpcm_ima_xbox</c>
    ///     decoder, which agrees with this implementation bit for bit. Reproduce with:
    ///     <c>ffmpeg -v error -i FILE -f s16le -acodec pcm_s16le - | sha256sum</c>
    ///     The six fixtures cover every sample rate in the corpus (11025 / 22050 /
    ///     44100 / 48000) and all three chunk layouts (data at 48, at 1028 behind a
    ///     bext broadcast header, and at 514 behind a JUNK chunk).
    /// </summary>
    [CorpusTheory]
    [InlineData("RollMetalGrating_11.pcm", 11025, 46848,
        "21d80b43f2ca876d803da7ed3275543eaa839fde8ef7b53d370b371ec062c76f")]
    [InlineData("GrindWireSpark.pcm", 22050, 46400,
        "dee2d481858465afd979fe771febb8029bf6ea97840627b8b09616b63b45a363")]
    [InlineData("CarBrakeSqueal.pcm", 44100, 30592,
        "1dc19d8d2e330e4a26ab360fb58c3ace4df58d98145b978e93327d76b1206ef4")]
    [InlineData("SK6_BA_SteveOElecLoop.pcm", 48000, 51712,
        "b4cf15817e59545c3d5411be51272eaa9ec41481392a8dead1bd8ef9fe96dbbf")]
    [InlineData("GrindMetalOff02.pcm", 44100, 32064,
        "f611dd27b09fc4b06c59d3effc968518c89aca71abf3e2851665e018eb58bc17")]
    [InlineData("SK6_BO_JesseRide_Grind.pcm", 44100, 132288,
        "41348ee826fa2a4cb19f4d9f3cf2ee59afa825730a671e647c68350fd8b16c10")]
    public void Decode_MatchesFfmpegReference(string fileName, int expectedRate, int expectedSamples, string sha256)
    {
        var path = _paths.FindSampleFile(XboxBuild, fileName);
        Assert.SkipWhen(path is null, $"{fileName} not present in Sample/Builds");

        var data = File.ReadAllBytes(path!);
        Assert.True(RiffWaveReader.TryRead(data, out var info));
        Assert.Equal(XboxImaAdpcm.FormatTag, info.FormatTag);
        Assert.Equal(expectedRate, info.SampleRate);

        var usable = info.DataLength - info.DataLength % info.BlockAlign;
        var pcm = XboxImaAdpcm.Decode(data.AsSpan(info.DataOffset, usable), info.Channels);

        Assert.Equal(expectedSamples, pcm.Length);

        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [CorpusFact]
    public void ConvertToWav_RealFixture_WritesAPlayableMonoWav()
    {
        var path = _paths.FindSampleFile(XboxBuild, "CarBrakeSqueal.pcm");
        Assert.SkipWhen(path is null, "CarBrakeSqueal.pcm not present in Sample/Builds");

        var outputDir = Path.Combine(Path.GetTempPath(), "nmt_pcm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = XboxPcmDecoder.ConvertToWav(path!, outputDir);
            Assert.True(result.Success, result.ErrorMessage);

            var wav = File.ReadAllBytes(Path.Combine(outputDir, "CarBrakeSqueal.wav"));
            Assert.True(RiffWaveReader.TryRead(wav, out var info));
            Assert.Equal(1, info.FormatTag); // written back out as plain PCM
            Assert.Equal(1, info.Channels);
            Assert.Equal(44100, info.SampleRate);
            Assert.Equal(30592 * 2, info.DataLength);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ConvertToWav_NonXboxAdpcm_FailsWithAClearReason()
    {
        // A plain PCM RIFF must not be silently run through the ADPCM decoder.
        var outputDir = Path.Combine(Path.GetTempPath(), "nmt_pcm_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outputDir);
            WavWriter.WritePcm16(Path.Combine(outputDir, "plain.wav"), 44100, 1, new short[64]);
            var data = File.ReadAllBytes(Path.Combine(outputDir, "plain.wav"));

            var result = XboxPcmDecoder.ConvertToWav(data, "plain_out", outputDir);

            Assert.False(result.Success);
            Assert.Contains("0x0069", result.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
    public void Decode_AllXboxPcm_MatchTheDeclaredBlockGeometry()
    {
        var files = _paths.FindSampleFiles(XboxBuild, "*.pcm").ToList();
        Assert.SkipWhen(files.Count == 0, "No .pcm files in Sample/Builds");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!RiffWaveReader.TryRead(data, out var info))
            {
                offenders.Add($"{Path.GetFileName(file)}: not a RIFF/WAVE");
                continue;
            }

            if (info.FormatTag != XboxImaAdpcm.FormatTag
                || info.Channels != 1
                || info.BlockAlign != XboxImaAdpcm.BlockAlignPerChannel
                || info.SamplesPerBlock != XboxImaAdpcm.SamplesPerBlock
                || info.DataLength % XboxImaAdpcm.BlockAlignPerChannel != 0)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)}: tag=0x{info.FormatTag:X4} ch={info.Channels} " +
                    $"align={info.BlockAlign} spb={info.SamplesPerBlock} data={info.DataLength}");
                continue;
            }

            var pcm = XboxImaAdpcm.Decode(data.AsSpan(info.DataOffset, info.DataLength), info.Channels);
            var expected = info.DataLength / XboxImaAdpcm.BlockAlignPerChannel * XboxImaAdpcm.SamplesPerBlock;
            if (pcm.Length != expected)
                offenders.Add($"{Path.GetFileName(file)}: decoded {pcm.Length}, expected {expected}");
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)));
    }

    /// <summary>
    ///     nAvgBytesPerSec == rate * 36 / 64 truncated, at every sample rate. This
    ///     is what makes "64 samples per 36-byte block" a tested property of the
    ///     format rather than an assumption carried by the decoder.
    /// </summary>
    [CorpusFact]
    public void Decode_AllXboxPcm_ByteRateImpliesSixtyFourSamplesPerBlock()
    {
        var files = _paths.FindSampleFiles(XboxBuild, "*.pcm").ToList();
        Assert.SkipWhen(files.Count == 0, "No .pcm files in Sample/Builds");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!RiffWaveReader.TryRead(data, out var info))
                continue;

            var expected = info.SampleRate * XboxImaAdpcm.BlockAlignPerChannel / XboxImaAdpcm.SamplesPerBlock;
            if (info.AvgBytesPerSec != expected)
                offenders.Add($"{Path.GetFileName(file)}: avg={info.AvgBytesPerSec}, expected {expected}");
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)));
    }
}
