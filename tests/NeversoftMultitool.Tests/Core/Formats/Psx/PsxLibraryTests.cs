using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public class PsxLibraryTests(TestPaths paths)
{
    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void ExtractTextures_Xbox_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputDir = paths.PsxXboxDir!;
        var goldenDir = paths.GoldenPsxXboxDir!;
        var inputFile = Path.Combine(inputDir, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PsxXbox_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, createSubDirs: false);

            Assert.True(result.TotalTextures > 0, $"No textures found in {filename}");
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            // Compare each output PNG against golden file
            var outputFiles = Directory.GetFiles(tempDir, "*.png");
            Assert.NotEmpty(outputFiles);

            foreach (var outputFile in outputFiles)
            {
                var outputName = Path.GetFileName(outputFile);
                var goldenFile = Path.Combine(goldenDir, outputName);
                Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found: {outputName}");

                var comparison = PixelComparer.CompareRgba(outputFile, goldenFile);
                Assert.True(comparison.Match, $"{outputName}: {comparison.Details}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    [InlineData("items.psx")]
    public void ExtractTextures_Ps1_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputDir = paths.PsxPs1Dir!;
        var goldenDir = paths.GoldenPsxPs1Dir!;
        var inputFile = Path.Combine(inputDir, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PsxPs1_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, createSubDirs: false);

            Assert.True(result.TotalTextures > 0, $"No textures found in {filename}");
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            var outputFiles = Directory.GetFiles(tempDir, "*.png");
            Assert.NotEmpty(outputFiles);

            foreach (var outputFile in outputFiles)
            {
                var outputName = Path.GetFileName(outputFile);
                var goldenFile = Path.Combine(goldenDir, outputName);
                Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found: {outputName}");

                var comparison = PixelComparer.CompareRgba(outputFile, goldenFile);
                Assert.True(comparison.Match, $"{outputName}: {comparison.Details}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractTextures_InvalidFile_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Invalid_" + Guid.NewGuid().ToString("N")[..8]);
        var tempFile = Path.Combine(tempDir, "invalid.psx");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00]);

            var result = PsxLibrary.ExtractTextures(tempFile, tempDir, createSubDirs: false);

            Assert.False(result.Success);
            Assert.Equal(0, result.TexturesWritten);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
