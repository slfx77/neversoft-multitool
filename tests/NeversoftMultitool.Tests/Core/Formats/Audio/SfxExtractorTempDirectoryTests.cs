using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxExtractorTempDirectoryTests
{
    [Fact]
    public void ExtractToWav_PreExistingLegacyTempDirectory_SurvivesSuccessfulExtraction()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_owned_temp");
        try
        {
            var outputDir = Path.Combine(tempDir, "output");
            var legacyTempDir = Path.Combine(outputDir, "demo", "__sfx_tmp");
            var keepPath = Path.Combine(legacyTempDir, "keep.bin");
            var keepBytes = new byte[] { 0xDE, 0xAD };
            Directory.CreateDirectory(legacyTempDir);
            File.WriteAllBytes(keepPath, keepBytes);

            var result = SfxExtractor.ExtractToWav(
                CreateSingleCue(),
                "demo",
                new SfxExtractor.SfxBankBytes(CreateSingleSampleKat(), "KAT"),
                outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, result.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDir, "demo", "000.wav")));
            Assert.Equal(keepBytes, File.ReadAllBytes(keepPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static byte[] CreateSingleCue()
    {
        var data = new byte[20];
        data[3] = 60;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x00B0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), uint.MaxValue);
        return data;
    }

    private static byte[] CreateSingleSampleKat()
    {
        const int entryOffset = 4;
        const int sampleOffset = 48;
        var data = new byte[sampleOffset + 4];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 4), sampleOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 12), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 20), 8);
        new byte[] { 0x01, 0x7F, 0x80, 0xFF }.CopyTo(data, sampleOffset);

        return data;
    }
}
