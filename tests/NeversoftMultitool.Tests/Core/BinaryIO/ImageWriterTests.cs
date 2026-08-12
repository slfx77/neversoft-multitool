using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public class ImageWriterTests
{
    [Fact]
    public void WritePng_InvalidRgbaBufferDoesNotCreateOutputDirectory()
    {
        var outputRoot = CreateAbsentOutputRoot();
        var outputPath = Path.Combine(outputRoot, "nested", "invalid.png");

        try
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                ImageWriter.WritePng(outputPath, 1, 1, new byte[3]));

            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public void WritePngRgb_InvalidRgbBufferDoesNotCreateOutputDirectory()
    {
        var outputRoot = CreateAbsentOutputRoot();
        var outputPath = Path.Combine(outputRoot, "nested", "invalid.png");

        try
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                ImageWriter.WritePngRgb(outputPath, 1, 1, new byte[2]));

            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    private static string CreateAbsentOutputRoot()
    {
        return Path.Combine(
            Path.GetTempPath(),
            nameof(ImageWriterTests),
            Guid.NewGuid().ToString("N"));
    }
}
