using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class KatExtractorTests
{
    private const int EntrySize = 44;

    [Fact]
    public void EnumerateSamples_CompletePayloads_ReturnsEverySample()
    {
        var samples = KatExtractor.EnumerateSamples(BuildTwoEntryKat(includeSecondPayload: true));

        Assert.Equal(2, samples.Count);
        Assert.Equal([0, 1], samples.Select(static sample => sample.Index));
        Assert.All(samples, static sample =>
        {
            Assert.Equal(2, sample.DataSize);
            Assert.Equal(22050, sample.SampleRate);
            Assert.Equal("PCM 16-bit", sample.Encoding);
        });
    }

    [Fact]
    public void EnumerateSamples_TruncatedSinglePayload_ReturnsEmpty()
    {
        Assert.Empty(KatExtractor.EnumerateSamples(BuildTruncatedSingleEntryKat()));
    }

    [Fact]
    public void EnumerateSamples_LaterTruncatedPayload_DoesNotPublishValidPrefix()
    {
        Assert.Empty(KatExtractor.EnumerateSamples(BuildTwoEntryKat(includeSecondPayload: false)));
    }

    [Fact]
    public void EnumerateSamples_EntryCountWhoseTableSizeWrapsUInt32_ReturnsEmpty()
    {
        Assert.Empty(KatExtractor.EnumerateSamples(BitConverter.GetBytes(0x40000000u)));
    }

    [Theory]
    [InlineData(4u, 5u, 10L)]
    [InlineData(8u, 5u, 5L)]
    [InlineData(16u, 5u, 2L)]
    [InlineData(0u, 5u, 2L)]
    public void EnumerateSamples_ReportsDecodedFramesForKnownEncodings(
        uint bitsPerSample,
        uint dataSize,
        long expectedFrames)
    {
        var sample = Assert.Single(KatExtractor.EnumerateSamples(
            BuildSingleEntryKat(dataSize, bitsPerSample)));

        Assert.Equal(expectedFrames, sample.DecodedFrameCount);
    }

    [Fact]
    public void EnumerateSamples_UnknownEncodingHasNoDecodedFrameCount()
    {
        var sample = Assert.Single(KatExtractor.EnumerateSamples(BuildSingleEntryKat(5, 12)));

        Assert.Null(sample.DecodedFrameCount);
    }

    [Fact]
    public void ExtractToWav_LaterPayloadIsTruncated_FailsBeforeWritingAnyFiles()
    {
        var outputDir = CreateTempPath();

        try
        {
            var result = KatExtractor.ExtractToWav(
                BuildTwoEntryKat(includeSecondPayload: false),
                "bank",
                outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Contains("entry 1", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(outputDir, "bank")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ExtractToWav_CompletePayloads_WritesEverySample()
    {
        var outputDir = CreateTempPath();

        try
        {
            var result = KatExtractor.ExtractToWav(
                BuildTwoEntryKat(includeSecondPayload: true),
                "bank",
                outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(2, result.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "000.wav")));
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "001.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ExtractSingleToWav_TruncatedPayload_ReturnsNullWithoutWriting()
    {
        var outputDir = CreateTempPath();

        try
        {
            var result = KatExtractor.ExtractSingleToWav(
                BuildTruncatedSingleEntryKat(),
                "bank",
                0,
                outputDir);

            Assert.Null(result);
            Assert.False(File.Exists(Path.Combine(outputDir, "bank_000.wav")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static byte[] BuildTwoEntryKat(bool includeSecondPayload)
    {
        const uint entryCount = 2;
        const uint sampleSize = 2;
        const uint sampleRate = 22050;
        const uint bitsPerSample = 16;
        const uint tableSize = 4 + entryCount * EntrySize;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(entryCount);
        WriteEntry(writer, tableSize, sampleSize, sampleRate, bitsPerSample);
        WriteEntry(writer, tableSize + sampleSize, sampleSize, sampleRate, bitsPerSample);
        writer.Write((short)0x1234);
        if (includeSecondPayload)
            writer.Write((short)0x5678);

        return stream.ToArray();
    }

    private static byte[] BuildTruncatedSingleEntryKat()
    {
        const uint tableSize = 4 + EntrySize;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u);
        WriteEntry(writer, tableSize, 4, 22050, 16);
        writer.Write((short)0x1234);
        return stream.ToArray();
    }

    private static byte[] BuildSingleEntryKat(uint dataSize, uint bitsPerSample)
    {
        const uint tableSize = 4 + EntrySize;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u);
        WriteEntry(writer, tableSize, dataSize, 22050, bitsPerSample);
        writer.Write(new byte[checked((int)dataSize)]);
        return stream.ToArray();
    }

    private static void WriteEntry(
        BinaryWriter writer,
        uint offset,
        uint size,
        uint sampleRate,
        uint bitsPerSample)
    {
        writer.Write(1u); // channels
        writer.Write(offset);
        writer.Write(size);
        writer.Write(sampleRate);
        writer.Write(0u); // loop
        writer.Write(bitsPerSample);
        writer.Write(0u); // unknown
        writer.Write(new byte[16]); // name
    }

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_KatExtractor_" + Guid.NewGuid().ToString("N"));
    }
}
