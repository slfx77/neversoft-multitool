using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool.Tests.Core.Formats.Rle;

public class RleImageTests(TestPaths paths)
{
    private const int DefaultWidth = 512;

    [Theory]
    [InlineData("legal.rle")]
    [InlineData("loadlogo.rle")]
    [InlineData("load01.bmr")]
    [InlineData("title.bmr")]
    public void Convert_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputFile = Path.Combine(paths.RleDir!, filename);
        var goldenFile = Path.Combine(paths.GoldenRleDir!, Path.ChangeExtension(filename, ".png"));
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");
        Assert.SkipWhen(!File.Exists(goldenFile), "Golden file not found");

        var result = RleImage.Convert(inputFile, DefaultWidth);
        Assert.True(result.Success, $"Conversion failed: {result.ErrorMessage}");

        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
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
    [InlineData("legal.rle")]
    [InlineData("loadlogo.rle")]
    [InlineData("load01.bmr")]
    [InlineData("title.bmr")]
    public void Convert_AutoDetect_MatchesExplicitWidth(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.RleDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var explicitResult = RleImage.Convert(inputFile, DefaultWidth);
        var autoResult = RleImage.Convert(inputFile);

        Assert.True(autoResult.Success, $"Auto-detect failed: {autoResult.ErrorMessage}");
        Assert.True(autoResult.WidthAutoDetected);
        Assert.Equal(explicitResult.Width, autoResult.Width);
        Assert.Equal(explicitResult.Height, autoResult.Height);
        Assert.Equal(explicitResult.RgbPixels, autoResult.RgbPixels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_UnsupportedExtension_ReturnsError(bool autoDetect)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.Create(tempFile).Dispose();
        try
        {
            var result = autoDetect ? RleImage.Convert(tempFile) : RleImage.Convert(tempFile, DefaultWidth);
            Assert.False(result.Success);
            Assert.Contains("Unsupported", result.ErrorMessage);
        }
        finally
        {
            if (File.Exists(tempFile))
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

    [Fact]
    public void DeclaredSizeSmallerThanHeader_DetectionFallsBackAndConversionFails()
    {
        byte[] data =
        [
            0x5F, 0x52, 0x4C, 0x45, 0x5F, 0x31, 0x36, 0x5F,
            0x07, 0x00, 0x00, 0x00,
            0x00, 0x82, 0x1F, 0x00
        ];

        Assert.Equal(DefaultWidth, RleImage.DetectWidth(data, "bad.rle"));

        var automatic = RleImage.Convert(data, "bad.rle");
        var explicitWidth = RleImage.Convert(data, "bad.rle", DefaultWidth);

        Assert.False(automatic.Success);
        Assert.False(explicitWidth.Success);
        Assert.Equal("RLE decompressed size 7 is smaller than the 8-byte header", automatic.ErrorMessage);
        Assert.Equal(automatic.ErrorMessage, explicitWidth.ErrorMessage);
        Assert.Equal(DefaultWidth, automatic.Width);
        Assert.Equal(DefaultWidth, explicitWidth.Width);
        Assert.True(automatic.WidthAutoDetected);
        Assert.False(explicitWidth.WidthAutoDetected);
    }

    [Fact]
    public void DeclaredSizeAtValidOneRowBoundary_ConvertsOneRow()
    {
        byte[] data =
        [
            0x5F, 0x52, 0x4C, 0x45, 0x5F, 0x31, 0x36, 0x5F,
            0x08, 0x04, 0x00, 0x00,
            0x00, 0x82, 0x1F, 0x00
        ];

        Assert.Equal(DefaultWidth, RleImage.DetectWidth(data, "valid.rle"));

        var result = RleImage.Convert(data, "valid.rle");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.WidthAutoDetected);
        Assert.Equal(DefaultWidth, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(DefaultWidth * 3, result.RgbPixels.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_InvalidZlb_ReturnsError(bool autoDetect)
    {
        var result = autoDetect
            ? RleImage.Convert("NOPE"u8.ToArray(), "bad.zlb")
            : RleImage.Convert("NOPE"u8.ToArray(), "bad.zlb", DefaultWidth);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(autoDetect ? 0 : DefaultWidth, result.Width);
        Assert.False(result.WidthAutoDetected);
    }

    [Theory]
    [InlineData(122_880, 512)] // 512x240 — standard PS1 half-height
    [InlineData(131_072, 512)] // 512x256
    [InlineData(307_200, 640)] // 640x480 — standard PS1 fullscreen (not 512x600)
    [InlineData(0, 512)] // fallback
    [InlineData(-1, 512)] // negative fallback
    public void GuessWidth_ReturnsExpectedWidth(long totalPixels, int expectedWidth)
    {
        Assert.Equal(expectedWidth, RleImage.GuessWidth(totalPixels));
    }
}
