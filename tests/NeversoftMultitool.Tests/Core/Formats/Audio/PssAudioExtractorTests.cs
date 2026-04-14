using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Core;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class PssAudioExtractorTests(TestPaths paths)
{
    private string RepresentativeSampleFile =>
        paths.SampleBuildsDir is null ? string.Empty : Path.Combine(
            paths.SampleBuildsDir,
            "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)",
            "MOVIES",
            "ATVI.PSS");

    [Fact]
    public void ConvertToWav_SyntheticPss_WritesExpectedStereoWave()
    {
        var samples = new short[] { 1000, -1000, 2000, -2000, 3000, -3000, 4000, -4000 };
        var pssBytes = PssTestBuilder.CreatePssWithAdsPcm(samples, sampleRate: 48000, channels: 2, interleave: 4);
        var pssPath = FormatProbeTestHelper.CreateTempFile(".pss", pssBytes);
        var outputDir = FormatProbeTestHelper.CreateTempDirectory("pss_extract");

        try
        {
            var result = PssAudioExtractor.ConvertToWav(pssPath, outputDir);

            Assert.True(result.Success, result.ErrorMessage);

            var wavPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(pssPath) + ".wav");
            Assert.True(File.Exists(wavPath));

            var wavBytes = File.ReadAllBytes(wavPath);
            Assert.True(wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
            Assert.True(wavBytes.AsSpan(8, 4).SequenceEqual("WAVE"u8));
            Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2)));
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4)));

            var decodedSamples = new short[samples.Length];
            Buffer.BlockCopy(wavBytes, 44, decodedSamples, 0, decodedSamples.Length * sizeof(short));
            Assert.Equal(samples, decodedSamples);
        }
        finally
        {
            File.Delete(pssPath);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void ProbeAudio_SyntheticPss_IsSupported()
    {
        var pssPath = FormatProbeTestHelper.CreateTempFile(
            ".pss",
            PssTestBuilder.CreatePssWithAdsPcm([1000, -1000, 2000, -2000], 48000, 2, 4));

        try
        {
            var result = FormatProbe.ProbeAudio(pssPath);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSS Audio", result.FormatName);
        }
        finally
        {
            File.Delete(pssPath);
        }
    }

    [Fact]
    public void Probe_RepresentativeSample_ReturnsEmbeddedAdsMetadata()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeSampleFile), "Representative PS2 PSS sample not found");

        var probe = PssAudioExtractor.Probe(RepresentativeSampleFile);

        Assert.NotNull(probe);
        Assert.Equal("ADS PCM16LE", probe!.CodecName);
        Assert.Equal(48000, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(0x200, probe.Interleave);
    }
}

internal static class PssTestBuilder
{
    public static byte[] CreatePssWithAdsPcm(short[] samples, int sampleRate, int channels, int interleave)
    {
        var pcmBody = CreateInterleavedPcmBody(samples, channels, interleave);
        var adsBytes = CreateAdsStream(pcmBody, sampleRate, channels, interleave);

        var firstPayloadLength = Math.Min(adsBytes.Length, 0x30);
        var firstPayload = adsBytes.AsSpan(0, firstPayloadLength).ToArray();
        var secondPayload = adsBytes.AsSpan(firstPayloadLength).ToArray();

        using var stream = new MemoryStream();
        WritePrivatePacket(stream, firstPayload);
        if (secondPayload.Length > 0)
            WritePrivatePacket(stream, secondPayload);
        return stream.ToArray();
    }

    private static byte[] CreateAdsStream(byte[] pcmBody, int sampleRate, int channels, int interleave)
    {
        var adsBytes = new byte[0x28 + pcmBody.Length];
        "SShd"u8.CopyTo(adsBytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(adsBytes.AsSpan(4, 4), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(adsBytes.AsSpan(8, 4), 0x01);
        BinaryPrimitives.WriteInt32LittleEndian(adsBytes.AsSpan(0x0C, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(adsBytes.AsSpan(0x10, 4), channels);
        BinaryPrimitives.WriteInt32LittleEndian(adsBytes.AsSpan(0x14, 4), interleave);
        BinaryPrimitives.WriteUInt32LittleEndian(adsBytes.AsSpan(0x18, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(adsBytes.AsSpan(0x1C, 4), 0xFFFFFFFF);
        "SSbd"u8.CopyTo(adsBytes.AsSpan(0x20, 4));
        BinaryPrimitives.WriteInt32LittleEndian(adsBytes.AsSpan(0x24, 4), pcmBody.Length);
        pcmBody.CopyTo(adsBytes.AsSpan(0x28));
        return adsBytes;
    }

    private static byte[] CreateInterleavedPcmBody(short[] samples, int channels, int interleave)
    {
        var samplesPerBlock = interleave / sizeof(short);
        var frames = (int)Math.Ceiling(samples.Length / (double)(samplesPerBlock * channels));
        var body = new byte[frames * channels * interleave];

        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var blockBase = ((frame * channels) + channel) * interleave;
                for (var sampleOffset = 0; sampleOffset < samplesPerBlock; sampleOffset++)
                {
                    var sourceIndex = (frame * samplesPerBlock * channels) + (sampleOffset * channels) + channel;
                    var value = sourceIndex < samples.Length ? samples[sourceIndex] : (short)0;
                    BinaryPrimitives.WriteInt16LittleEndian(
                        body.AsSpan(blockBase + (sampleOffset * sizeof(short)), sizeof(short)),
                        value);
                }
            }
        }

        return body;
    }

    private static void WritePrivatePacket(Stream stream, byte[] payload)
    {
        stream.Write([0x00, 0x00, 0x01, 0xBD]);
        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, checked((ushort)(payload.Length + 3)));
        stream.Write(lengthBytes);
        stream.Write([0xA0, 0x00, 0x00]);
        stream.Write(payload);
    }
}
