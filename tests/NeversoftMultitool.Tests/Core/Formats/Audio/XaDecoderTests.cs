using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class XaDecoderTests
{
    [Fact]
    public void ConvertToWav_EmptyInputFailsWithoutCreatingOutput()
    {
        var outputDir = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_XaDecoder_" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = XaDecoder.ConvertToWav([], "empty", outputDir);

            Assert.False(result.Success);
            Assert.Equal(0, result.SamplesWritten);
            Assert.Contains("Unrecognized XA format", result.ErrorMessage);
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }
}
