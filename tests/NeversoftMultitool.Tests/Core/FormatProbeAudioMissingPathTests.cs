using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeAudioMissingPathTests
{
    [Theory]
    [InlineData(".xa", "XA Audio")]
    [InlineData(".vab", "VAB Sound Bank")]
    [InlineData(".vag", "VAG Audio")]
    [InlineData(".kat", "KAT Sound Bank")]
    public void ProbeAudio_MissingExtensionOnlyFile_IsUnsupported(
        string extension,
        string expectedFormatName)
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}{extension}");

        var result = FormatProbe.ProbeAudio(missingPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal(expectedFormatName, result.FormatName);
        Assert.Equal("File not found", result.UnsupportedReason);
    }

    [Theory]
    [InlineData(".xa", "XA Audio")]
    [InlineData(".vab", "VAB Sound Bank")]
    [InlineData(".vag", "VAG Audio")]
    [InlineData(".kat", "KAT Sound Bank")]
    public void ProbeAudio_ExistingExtensionOnlyFile_PreservesSupportedLabel(
        string extension,
        string expectedFormatName)
    {
        var filePath = FormatProbeTestHelper.CreateTempFile(extension, [0x00]);
        try
        {
            var result = FormatProbe.ProbeAudio(filePath);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal(expectedFormatName, result.FormatName);
            Assert.Null(result.UnsupportedReason);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
