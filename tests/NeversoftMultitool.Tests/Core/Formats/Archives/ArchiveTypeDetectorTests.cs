using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public sealed class ArchiveTypeDetectorTests
{
    [Theory]
    [InlineData("nested.pak.zip", ".zip", true)]
    [InlineData("bundle.pre.pkr", ".pkr", true)]
    [InlineData("level.pak.ps2", ".pak", true)]
    [InlineData("hair.tex.zip.wpc", ".zip", true)]
    [InlineData("nested.pak.zip.wpc", ".zip", true)]
    [InlineData("level.pak.ps2.bak", ".bak", false)]
    public void GetArchiveExtension_PrefersOuterArchiveBeforePlatformSuffix(
        string fileName,
        string expectedExtension,
        bool expectedArchive)
    {
        Assert.Equal(expectedExtension, ArchiveTypeDetector.GetArchiveExtension(fileName));
        Assert.Equal(expectedArchive, ArchiveTypeDetector.IsArchiveFile(fileName));
    }
}
