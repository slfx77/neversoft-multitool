using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class AdxDecoderTests
{
    private const int HeaderSize = 18;
    private const int BlockSize = 18;
    private const int SamplesPerFrame = 32;
    private const int SampleRate = 44100;

    [Fact]
    public void ConvertToWav_DeclaredFrameWithoutPayload_FailsWithoutOutput()
    {
        var data = CreateAdx(includeFrame: false);
        var outputDir = CreateTempPath();

        try
        {
            var result = AdxDecoder.ConvertToWav(data, "truncated", outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Equal(
                "ADX payload is truncated: header declares 32 samples per channel " +
                "requiring 18 bytes, but only 0 bytes are available.",
                result.ErrorMessage);
            Assert.False(Directory.Exists(outputDir));
            Assert.False(File.Exists(Path.Combine(outputDir, "truncated.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ConvertToWav_CompleteZeroFrame_WritesDeclaredSamples()
    {
        var data = CreateAdx(includeFrame: true);
        var outputDir = CreateTempPath();

        try
        {
            var result = AdxDecoder.ConvertToWav(data, "complete", outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);
            Assert.Equal(108, new FileInfo(Path.Combine(outputDir, "complete.wav")).Length);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ConvertToWav_DataOffsetInsideHeader_FailsWithoutOutput()
    {
        var data = CreateAdx(includeFrame: true);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0);
        var outputDir = CreateTempPath();

        try
        {
            var result = AdxDecoder.ConvertToWav(data, "bad-offset", outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Equal("Invalid ADX data offset: 4", result.ErrorMessage);
            Assert.False(Directory.Exists(outputDir));
            Assert.False(File.Exists(Path.Combine(outputDir, "bad-offset.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static byte[] CreateAdx(bool includeFrame)
    {
        var data = new byte[HeaderSize + (includeFrame ? BlockSize : 0)];
        BinaryPrimitives.WriteUInt16BigEndian(data, 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), HeaderSize - 4);
        data[4] = 3;
        data[5] = BlockSize;
        data[6] = 4;
        data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), SampleRate);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), SamplesPerFrame);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), 500);
        return data;
    }

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_AdxDecoder_" + Guid.NewGuid().ToString("N"));
    }
}
