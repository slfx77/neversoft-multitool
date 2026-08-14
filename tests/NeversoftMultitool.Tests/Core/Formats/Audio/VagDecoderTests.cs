using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class VagDecoderTests
{
    private const int HeaderSize = 48;
    private const int BlockSize = 16;
    private const int SampleRate = 22050;

    [Fact]
    public void DeclaredPayloadIsTruncated_ProbeAndConversionRejectWithoutOutput()
    {
        var data = CreateVag(declaredPayloadSize: BlockSize * 2, actualPayloadSize: BlockSize);
        var tempRoot = CreateTempPath();
        Directory.CreateDirectory(tempRoot);
        var inputPath = Path.Combine(tempRoot, "truncated.vag");
        var byteOutputDir = Path.Combine(tempRoot, "byte-output");
        var pathOutputDir = Path.Combine(tempRoot, "path-output");
        File.WriteAllBytes(inputPath, data);

        try
        {
            Assert.Null(VagDecoder.Probe(data));
            Assert.Null(VagDecoder.Probe(inputPath));

            var byteResult = VagDecoder.ConvertToWav(data, "truncated", byteOutputDir);
            var pathResult = VagDecoder.ConvertToWav(inputPath, pathOutputDir);
            const string expectedError =
                "VAG payload is truncated: header declares 32 bytes, but only 16 bytes are available.";

            Assert.False(byteResult.Success);
            Assert.Equal(0, byteResult.SamplesWritten);
            Assert.Equal(expectedError, byteResult.ErrorMessage);
            Assert.False(pathResult.Success);
            Assert.Equal(0, pathResult.SamplesWritten);
            Assert.Equal(expectedError, pathResult.ErrorMessage);
            Assert.False(Directory.Exists(byteOutputDir));
            Assert.False(File.Exists(Path.Combine(byteOutputDir, "truncated.wav")));
            Assert.False(Directory.Exists(pathOutputDir));
            Assert.False(File.Exists(Path.Combine(pathOutputDir, "truncated.wav")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CompleteDeclaredPayload_WithTrailingBlock_UsesOnlyDeclaredPayload()
    {
        var data = CreateVag(declaredPayloadSize: BlockSize, actualPayloadSize: BlockSize * 2);
        var outputDir = CreateTempPath();

        try
        {
            var probe = VagDecoder.Probe(data);
            var result = VagDecoder.ConvertToWav(data, "complete", outputDir);

            Assert.NotNull(probe);
            Assert.True(probe.HasHeader);
            Assert.Equal(28 / (double)SampleRate, probe.DurationSeconds);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);
            Assert.Equal(44 + 28 * sizeof(short), new FileInfo(Path.Combine(outputDir, "complete.wav")).Length);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Probe_StopsAtEndMarkerBeforeDeclaredPadding()
    {
        var data = CreateVag(
            declaredPayloadSize: BlockSize * 3,
            actualPayloadSize: BlockSize * 3);
        data[HeaderSize + BlockSize + 1] = SpuAdpcm.FlagEnd;

        var probe = VagDecoder.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(2 * 28 / (double)SampleRate, probe.DurationSeconds);
        Assert.Equal(probe.DurationSeconds, AudioDurationProbe.Probe("VAG", data));
    }

    private static byte[] CreateVag(int declaredPayloadSize, int actualPayloadSize)
    {
        var data = new byte[HeaderSize + actualPayloadSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x56414770);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), 0x20);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12, 4), declaredPayloadSize);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16, 4), SampleRate);
        return data;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_VagDecoder_" + Guid.NewGuid().ToString("N"));
    }
}
