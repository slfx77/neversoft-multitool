using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Rle;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public class RleImageTests(TestPaths paths)
{
    private const int DefaultWidth = 512;

    [Theory]
    [InlineData("legal.rle")]
    [InlineData("loadlogo.rle")]
    public void Convert_RleFiles_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputFile = Path.Combine(paths.RleDir!, filename);
        var goldenFile = Path.Combine(paths.GoldenRleDir!, Path.ChangeExtension(filename, ".png"));
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");
        Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found");

        var result = RleImage.Convert(inputFile, DefaultWidth);
        Assert.True(result.Success, $"Conversion failed: {result.ErrorMessage}");

        var tempFile = Path.GetTempFileName() + ".png";
        try
        {
            ImageWriter.WritePngRgb(tempFile, result.Width, result.Height, result.RgbPixels);
            var comparison = PixelComparer.CompareRgb(tempFile, goldenFile);
            Assert.True(comparison.Match, $"{filename}: {comparison.Details}");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("load01.bmr")]
    [InlineData("title.bmr")]
    public void Convert_BmrFiles_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputFile = Path.Combine(paths.RleDir!, filename);
        var goldenFile = Path.Combine(paths.GoldenRleDir!, Path.ChangeExtension(filename, ".png"));
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");
        Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found");

        var result = RleImage.Convert(inputFile, DefaultWidth);
        Assert.True(result.Success, $"Conversion failed: {result.ErrorMessage}");

        var tempFile = Path.GetTempFileName() + ".png";
        try
        {
            ImageWriter.WritePngRgb(tempFile, result.Width, result.Height, result.RgbPixels);
            var comparison = PixelComparer.CompareRgb(tempFile, goldenFile);
            Assert.True(comparison.Match, $"{filename}: {comparison.Details}");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Convert_UnsupportedExtension_ReturnsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var result = RleImage.Convert(tempFile, DefaultWidth);
            Assert.False(result.Success);
            Assert.Contains("Unsupported", result.ErrorMessage);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Convert_InvalidRle_ReturnsError()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "bad.rle");
        try
        {
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            var result = RleImage.Convert(tempFile, DefaultWidth);
            Assert.False(result.Success);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
