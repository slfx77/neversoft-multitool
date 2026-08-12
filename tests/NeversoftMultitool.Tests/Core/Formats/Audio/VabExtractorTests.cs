using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class VabExtractorTests
{
    [Fact]
    public void ExtractToWav_LaterPayloadIsTruncated_FailsBeforeWritingAnyFiles()
    {
        var outputDir = CreateTempPath();
        var truncated = SfxTestBuilder.CreateVab([16, 32])[..^16];

        try
        {
            var result = VabExtractor.ExtractToWav(truncated, "bank", outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Contains("sample 2", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ExtractSingleToWav_SelectedPayloadIsTruncated_ReturnsNullWithoutWriting()
    {
        var outputDir = CreateTempPath();
        var truncated = SfxTestBuilder.CreateVab([16, 32])[..^16];

        try
        {
            var outputPath = VabExtractor.ExtractSingleToWav(truncated, "bank", 2, outputDir);

            Assert.Null(outputPath);
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void CompletePayloads_BatchAndSingleExtractionWriteExpectedFiles()
    {
        var outputDir = CreateTempPath();
        var data = SfxTestBuilder.CreateVab([16, 32]);

        try
        {
            var batchResult = VabExtractor.ExtractToWav(data, "bank", outputDir);
            var singlePath = VabExtractor.ExtractSingleToWav(data, "bank", 2, outputDir);

            Assert.True(batchResult.Success, batchResult.ErrorMessage);
            Assert.Equal(2, batchResult.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "001.wav")));
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "002.wav")));
            Assert.Equal(Path.Combine(outputDir, "bank_002.wav"), singlePath);
            Assert.True(File.Exists(singlePath));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void SparseCompletePayloads_SkipZeroSlotAndUsePackedSampleThreeOffset()
    {
        var outputDir = CreateTempPath();
        var data = SfxTestBuilder.CreateVab([16, 0, 32]);

        try
        {
            var batchResult = VabExtractor.ExtractToWav(data, "bank", outputDir);
            var singlePath = VabExtractor.ExtractSingleToWav(data, "bank", 3, outputDir);

            Assert.True(batchResult.Success, batchResult.ErrorMessage);
            Assert.Equal(2, batchResult.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "001.wav")));
            Assert.False(File.Exists(Path.Combine(outputDir, "bank", "002.wav")));
            Assert.True(File.Exists(Path.Combine(outputDir, "bank", "003.wav")));
            Assert.Equal(Path.Combine(outputDir, "bank_003.wav"), singlePath);
            Assert.True(File.Exists(singlePath));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void SparseLaterPayloadIsTruncated_BatchAndSingleWriteNothing()
    {
        var outputDir = CreateTempPath();
        var truncated = SfxTestBuilder.CreateVab([16, 0, 32])[..^16];

        try
        {
            var batchResult = VabExtractor.ExtractToWav(truncated, "bank", outputDir);
            var singlePath = VabExtractor.ExtractSingleToWav(truncated, "bank", 3, outputDir);

            Assert.False(batchResult.Success);
            Assert.Equal(0, batchResult.SamplesWritten);
            Assert.Contains("sample 3", batchResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Null(singlePath);
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_VabExtractor_" + Guid.NewGuid().ToString("N"));
    }
}
