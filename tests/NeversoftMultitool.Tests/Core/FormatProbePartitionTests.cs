using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbePartitionTests
{
    [Fact]
    public void PartitionFiles_SeparatesSupportedAndUnsupported()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("NsMultitool_Test_Probe");
        try
        {
            var supportedFile = Path.Combine(tempDir, "good.tex");
            File.WriteAllBytes(supportedFile, BitConverter.GetBytes(3u));

            var unsupportedFile = Path.Combine(tempDir, "bad.tex");
            File.WriteAllBytes(unsupportedFile, BitConverter.GetBytes(999u));

            var files = new[] { supportedFile, unsupportedFile };
            var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeTexture);

            Assert.Single(supported);
            Assert.Single(unsupported);
            Assert.Contains("good.tex", Path.GetFileName(supported[0]));
            Assert.Equal("bad.tex", unsupported[0].FileName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
